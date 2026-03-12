## 목차

- [아키텍처 개요](#아키텍처-개요)
- [시작하기](#시작하기)
- [프로젝트 구조](#프로젝트-구조)
- [서버 역할 정의](#서버-역할-정의)
- [네트워크 이원론](#네트워크-이원론)
- [패킷 구조](#패킷-구조)
- [세션 생명주기](#세션-생명주기)
- [메시지 처리 파이프라인](#메시지-처리-파이프라인)
- [동시성 모델](#동시성-모델)
- [Service Mesh RPC 흐름](#service-mesh-rpc-흐름)
- [성능 최적화](#성능-최적화)
- [사용 기술](#사용-기술)

---

## 아키텍처 개요

시스템은 **두 개의 물리적으로 분리된 네트워크 평면**으로 구성됩니다.

```
  ┌──────────┐    TCP     ┌───────────────┐  NetMQ P2P   ┌──────────────────┐
  │  Client   │◄─────────►│    Gateway     │◄────────────►│   GameServer     │
  │  (Unity)  │           │  (Dumb Proxy)  │  Data Plane  │  (Smart Hub/BFF) │
  └──────────┘            └───────────────┘              └────────┬─────────┘
                                                                  │
                           ┌──────────────────────────────────────┤  NetMQ
                           │         Node Service Mesh            │  Router-Router
                           │          (Control Plane)             │  Full Mesh
                           ▼                                      ▼
                    ┌──────────────┐                     ┌──────────────┐
                    │  Stateless   │ ◄──── RPC ────────► │  Stateless   │
                    │  Service A   │                     │  Service B   │
                    └──────────────┘                     └──────────────┘
```

- **Data Plane** — 클라이언트 게임 패킷 전송 (TCP + NetMQ P2P, 초저지연)
- **Control Plane** — 서버 간 제어 통신 (NetMQ Router-Router Full Mesh, RPC)

두 평면은 소켓과 라우팅 로직을 공유하지 않습니다.

---

## 시작하기

### 사전 요구사항

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Redis](https://redis.io/)
- [.NET Aspire workload](https://learn.microsoft.com/en-us/dotnet/aspire/) (선택)

### .NET Aspire로 실행 (권장)

```bash
dotnet workload install aspire

cd Sample/Sample.AppHost
dotnet run
```

Aspire가 Redis 컨테이너 프로비저닝, 포트 할당, 환경변수 주입, 관측성 대시보드를 자동으로 처리합니다.

### 수동 실행

```bash
# 터미널 1 — Redis
redis-server

# 터미널 2 — GameServer
cd Sample/GameSample && dotnet run

# 터미널 3 — Gateway
cd Sample/GatewaySample && dotnet run

# 터미널 4 — 테스트 클라이언트
cd Sample/TestClient && dotnet run
```

### 테스트

```bash
dotnet test
```

---

## 프로젝트 구조

```
NetworkEngine.Merged/
│
├── SimpleNetEngine.Protocol        (Tier 0 — 프로토콜)
│   ├── Packets/                    패킷 정의 및 Wire Format
│   ├── ErrorCodes/                 계층형 5자리 에러 코드
│   └── Messages/                   Protobuf 메시지 계약
│
├── SimpleNetEngine.Infrastructure  (Tier 1 — 인프라)
│   ├── Redis/                      StackExchange.Redis 래퍼
│   ├── Telemetry/                  OpenTelemetry + Serilog
│   └── Security/                   JWT 토큰 유틸리티
│
├── SimpleNetEngine.Node            (Tier 2 — Service Mesh)
│   ├── Network/                    NodeCommunicator, NodePacketRouter
│   ├── Core/                       NodeEventHandler 동시성 계층
│   ├── Dispatch/                   INodeDispatcher
│   └── Actor/                      INodeActor, INodeActorManager
│
├── SimpleNetEngine.Gateway         (Tier 3 — Gateway)
│   ├── Network/                    GatewaySession, GameSessionChannel
│   └── Core/                       GatewayNodeEventHandler
│
├── SimpleNetEngine.Game            (Tier 3 — GameServer)
│   ├── Network/                    GameSessionChannelListener
│   ├── Controllers/                Login, Kickout, Reconnect
│   ├── Actor/                      유저별 직렬화 큐
│   ├── Middleware/                  요청 처리 파이프라인
│   └── Core/                       GameNodeEventHandler
│
├── SimpleNetEngine.Client          (클라이언트 라이브러리)
├── PacketParserGenerator           (Roslyn Source Generator)
│
├── Sample/
│   ├── GameSample/                 GameServer 실행 프로젝트
│   ├── GatewaySample/              Gateway 실행 프로젝트
│   ├── NodeSample/                 Stateless Service 실행 프로젝트
│   ├── TestClient/                 테스트 클라이언트
│   ├── Protocol.Sample.User/      Client ↔ GameServer 프로토콜
│   ├── Protocol.Sample.Node/      Service Mesh RPC 프로토콜
│   └── Sample.AppHost/            .NET Aspire 오케스트레이션
│
└── Tests/
    ├── SimpleNetEngine.Tests.Game/
    └── SimpleNetEngine.Tests.Gateway/
```

### 계층 의존성 규칙

하위 계층은 상위 계층을 참조할 수 없습니다.

```
Tier 0  Protocol         ← 의존성 없음
  ▲
Tier 1  Infrastructure   ← Protocol
  ▲
Tier 2  Node             ← Infrastructure, Protocol
  ▲
Tier 3  Gateway / Game   ← Node, Infrastructure, Protocol
```

---

## 서버 역할 정의

### Gateway — Dumb Proxy

클라이언트와 고정된(pinned) GameServer 사이에서 바이트를 전달하는 투명 프록시입니다.
TCP 소켓 관리, 패킷 전달, 암호화/압축 오프로딩을 담당합니다.

### GameServer — Smart Hub / BFF

모든 클라이언트 요청의 진입점이자 중앙 허브입니다.
패킷 파싱, Redis SSOT 세션 검증, Gateway pinning 지시, 중복 로그인 처리, Stateful 게임 로직(전투, 이동)을 담당하며, Stateless 로직은 Service Mesh로 위임합니다.

### Stateless Service — 내부 마이크로서비스

Service Mesh를 통해서만 접근 가능한 순수 비즈니스 로직 처리기입니다.
GameServer의 인증을 신뢰하고, RPC로 수신된 요청에 대해 비즈니스 로직(상점, 우편함 등)을 처리합니다.

---

## 네트워크 이원론

### Data Plane (게임 세션 채널)

| 항목 | 사양 |
|------|------|
| 용도 | 클라이언트 게임 데이터 전송 (초저지연) |
| 토폴로지 | 1:N Star (Gateway 중심) |
| 프로토콜 | TCP (Client↔Gateway) + NetMQ Router-Router P2P (Gateway↔GameServer) |
| 라우팅 | Session 기반 Pinning — 최초 배정 후 고정 |
| 최적화 | Zero-Copy, Headroom 사전 할당 |

### Control Plane (Node Service Mesh)

| 항목 | 사양 |
|------|------|
| 용도 | 서버 간 제어/관리 RPC |
| 토폴로지 | Full Mesh (모든 노드가 직접 연결) |
| 프로토콜 | NetMQ Router-Router (Request-Response 패턴) |
| 라우팅 | NodeId 기반 — Redis Registry로 자동 피어 탐색 |
| Node ID | 12비트 동적 생성 |

---

## 패킷 구조

시스템은 **4개의 헤더**와 **3개의 복합 패킷 레이아웃**으로 구성됩니다.
모든 헤더는 `StructLayout(Sequential, Pack=1)` + **Little-Endian**입니다.

### 헤더 정의

#### EndPointHeader (12B) — TCP 스트림 프레이밍

```
┌─────────────┬───────────┬───────┬──────────┬────────────────┐
│ TotalLength │ ErrorCode │ Flags │ Reserved │ OriginalLength │
│ int (4B)   │ short (2B)│ 1B    │ 1B       │ int (4B)      │
└─────────────┴───────────┴───────┴──────────┴────────────────┘
```

- **TotalLength** — 헤더 포함 패킷 전체 길이
- **ErrorCode** — 0=정상, non-zero=에러 응답 (Payload 없음)
- **Flags** — `0x01`=Encrypted, `0x02`=Compressed, `0x04`=Handshake
- **OriginalLength** — 압축 전 원본 크기 (FlagCompressed일 때만 유효)

#### GameHeader (8B) — 게임 메시지 식별

```
┌────────────┬──────────────┬─────────────┐
│ MsgId      │ SequenceId   │ RequestId   │
│ int (4B)  │ ushort (2B)  │ ushort (2B) │
└────────────┴──────────────┴─────────────┘
```

- **MsgId** — Opcode (메시지 종류)
- **SequenceId** — 순서 번호 (멱등성 보장)
- **RequestId** — RPC 요청-응답 매칭

#### GSCHeader (49B) — Gateway ↔ GameServer 내부망 브릿지

```
┌──────┬───────────────┬──────────────┬───────────┬──────────────────┬──────────┐
│ Type │ GatewayNodeId │ SourceNodeId │ SessionId │ TraceId          │ SpanId   │
│ 1B   │ long (8B)    │ long (8B)    │ long (8B) │ Guid (16B)      │ long (8B)│
└──────┴───────────────┴──────────────┴───────────┴──────────────────┴──────────┘
```

- **Type** — `ClientPacket(1)`, `ServerPacket(2)`, `Control(3)`, `ClientConnected(4)`
- **GatewayNodeId / SourceNodeId** — 노드 라우팅 식별
- **SessionId** — 클라이언트 세션 식별
- **TraceId / SpanId** — OpenTelemetry 분산 추적

#### NodeHeader (57B) — Node Service Mesh RPC

```
┌──────────┬──────────┬──────────┬────────┬─────────┬────────────┬──────────────────┬──────────┐
│ Dest     │ Source   │ ActorId  │ MsgId  │ IsReply │ RequestKey │ TraceId          │ SpanId   │
│ long(8B) │ long(8B) │ long(8B) │ int(4B)│ 1B      │ int (4B)  │ Guid (16B)      │ long(8B) │
└──────────┴──────────┴──────────┴────────┴─────────┴────────────┴──────────────────┴──────────┘
```

- **Dest / Source** — 목적지/출발지 NodeId
- **ActorId** — 0=서버 전역, >0=특정 유저 큐로 라우팅
- **IsReply** — 0=요청, 1=응답
- **RequestKey** — RPC 요청-응답 매칭 (RequestCache에서 완료 추적)
- **TraceId / SpanId** — OpenTelemetry 분산 추적

### 복합 패킷 레이아웃

#### 1. 클라이언트 패킷 (Client ↔ Gateway, TCP)

```
[EndPointHeader (12B)][GameHeader (8B)][Protobuf Payload (가변)]
```

- Gateway는 EndPointHeader만 읽고, 나머지는 건드리지 않고 전달
- 암호화 시: `[EndPointHeader][AES-GCM Tag (16B)][Ciphertext]` (GameHeader+Payload 전체 암호화)
- 압축 시: `[EndPointHeader][LZ4 압축 데이터]` (OriginalLength에 원본 크기 기록)

#### 2. Game Session Channel 패킷 (Gateway ↔ GameServer, NetMQ P2P)

```
[GSCHeader (49B)][EndPointHeader (12B)][GameHeader (8B)][Protobuf Payload (가변)]
```

- Gateway가 GSCHeader를 prepend하여 세션/노드 라우팅 정보 첨부
- GameServer는 offset 49에서 EndPointHeader, offset 61에서 GameHeader를 읽음
- MsgId 추출 위치: `GSCHeader.Size + EndPointHeader.Size = offset 61`

#### 3. Node 패킷 (Node ↔ Node, Service Mesh RPC)

```
[NodeHeader (57B)][Protobuf Payload (가변)]
```

- `Msg.InitPool(57 + payloadLen)`으로 단일 버퍼에 할당
- `MemoryMarshal.Write`로 헤더 직접 기록 (직렬화 오버헤드 없음)
- Payload 접근: `Msg.Slice(NodeHeader.Size, Msg.Size - NodeHeader.Size)`

---

## 세션 생명주기

### 1. 신규 로그인

```
Client ──► Gateway ──► GameServer
                          │
                          ├── Redis 조회 → 기록 없음
                          ├── 세션 + 토큰 생성
                          ├── Redis 저장 (UserId → NodeId, SessionId, Token)
                          ├── Gateway에 Session Pinning 지시
                          └── 로그인 성공 응답
```

### 2. 중복 로그인 (다른 기기에서 동일 계정)

```
Client B ──► Gateway ──► GameServer 2
                            │
                            ├── Redis 조회 → GS1에 기존 세션 발견
                            ├── GS1에 KickoutRequest (Service Mesh RPC)
                            ├── GS1이 Client A 강제 종료 → KickoutAck 반환
                            ├── 기존 세션 삭제, 새 세션 생성
                            ├── Redis 갱신
                            └── 로그인 성공 응답
```

### 3. 재접속

```
Client ──► Gateway ──► GameServer
                          │
                          ├── SessionToken으로 Redis 조회
                          ├── 세션 유효성 확인
                          ├── 세션 복원 (재인증 불필요)
                          ├── Gateway Re-pinning
                          └── 재접속 성공 응답
```

---

## 메시지 처리 파이프라인

### Data Plane — 클라이언트 패킷 처리

```
Client TCP 수신
  ↓
GatewaySession (TCP 세션)
  ↓
GameSessionChannel (NetMQ P2P 전달, zero-copy)
  ↓
GameSessionChannelListener (GameServer 수신)
  ↓
MiddlewarePipeline (AOP 패턴)
  ├── LoggingMiddleware        (요청/응답 로깅)
  ├── PerformanceMiddleware    (지연시간 측정)
  ├── ExceptionHandlingMiddleware (예외 처리)
  └── 커스텀 미들웨어
  ↓
MessageDispatcher (Opcode 기반 라우팅)
  ↓
[UserController] 핸들러 실행
```

### Control Plane — Node RPC 처리

```
NodeCommunicator (NetMQ Router 소켓)
  ↓ OnProcessPacket 이벤트
NodePacketRouter (이벤트 구독자)
  ├── IsReply==1 → RequestCache.TryReply (RPC 응답 완료)
  └── IsReply==0 → NodeEventHandler.ProcessPacket
      ↓ 동시성 모델 선택
      ├── Sequential  → 전역 큐 순차 처리
      ├── Parallel    → 비동기 병렬 처리
      └── Per-User    → 유저별 직렬화 큐
          ↓
      INodeDispatcher.DispatchAsync
          ↓
      [NodeController] 핸들러 실행
```

### 컨트롤러 패턴

`[UserController]`와 `[NodeController]` 어트리뷰트로 ASP.NET 스타일의 패킷 핸들러를 정의합니다.
Roslyn Source Generator가 컴파일 타임에 DI 등록 코드를 자동 생성합니다.

```csharp
// 클라이언트 패킷 핸들러 — [UserController] + [UserPacketHandler]
// 시그니처: Task<Response> Handler(ISessionActor actor, TRequest req)
[UserController]
public class EchoController(ILogger<EchoController> logger)
{
    [UserPacketHandler(EchoReq.MsgId)]
    public Task<Response> EchoHandler(ISessionActor actor, EchoReq req)
    {
        logger.LogInformation(
            "Echo request: ActorId={ActorId}, UserId={UserId}, Message={Message}",
            actor.ActorId, actor.UserId, req.Message);

        return Task.FromResult(Response.Ok(new EchoRes
        {
            Message = $"Echo: {req.Message}",
            Timestamp = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }).UseEncrypt());
    }
}

// Node RPC 핸들러 — [NodeController] + [NodePacketHandler]
// 시그니처: Task<TResponse> Handler(TRequest req)
[NodeController]
public class ConnectionController(
    ILogger<ConnectionController> logger,
    ISessionActorManager actorManager)
{
    [NodePacketHandler(ServiceMeshNewUserNtfReq.MsgId)]
    public Task<ServiceMeshNewUserNtfRes> HandleNtfNewUser(ServiceMeshNewUserNtfReq req)
    {
        logger.LogInformation("NtfNewUser: Gateway={GatewayNodeId}, SessionId={SessionId}",
            req.GatewayNodeId, req.SessionId);

        // Actor 생성, 세션 등록 등 처리
        return Task.FromResult(new ServiceMeshNewUserNtfRes { Success = true });
    }
}
```

---

## 동시성 모델

핸들러별로 선택 가능한 3가지 동시성 모드:

```
                       ┌──────────────────────────────────────┐
                       │      NodeEventHandler (abstract)     │
                       │      ProcessPacket(NodePacket)       │
                       └──────────────┬───────────────────────┘
                                      │
           ┌──────────────────────────┼──────────────────────────┐
           ▼                          ▼                          ▼
┌────────────────────────┐ ┌───────────────────────┐ ┌────────────────────────┐
│ SequentialNodeEvent    │ │ ParallelNodeEvent     │ │ ActorNodeEvent         │
│ Handler (abstract)     │ │ Handler (abstract)    │ │ Handler (abstract)     │
├────────────────────────┤ ├───────────────────────┤ ├────────────────────────┤
│ • 전역 큐 순차 처리    │ │ • 요청별 독립 Task    │ │ • ActorId별 전용 큐    │
│ • 하나씩 처리          │ │ • 최대 처리량         │ │ • Push(pkt) 수신       │
│ • 단순하고 안전        │ │ • Fire-and-Forget     │ │ • Lock-free Channel    │
├────────────────────────┤ ├───────────────────────┤ ├────────────────────────┤
│ GameNodeEventHandler   │ │ StatelessEvent        │ │ ActorEventController   │
│ GatewayNodeEventHandler│ │ Controller            │ │                        │
└────────────────────────┘ └───────────────────────┘ └────────────────────────┘
```

| 모델 | 구현 클래스 | 적합한 시나리오 |
|------|------------|----------------|
| **Sequential** | `GameNodeEventHandler`, `GatewayNodeEventHandler` | 전역 상태가 중요하고 순서 필요 |
| **Parallel** | `StatelessEventController` | 독립 요청의 최대 처리량 (상점, 우편함) |
| **ActorSerialized** | `ActorEventController` | 유저별 순서 보장 + 유저 간 병렬 (전투, 이동) |

### Per-User Serialized 상세

UserId를 키로 전용 `Channel<T>` 큐를 할당하여, 글로벌 락 없이 유저 단위 메시지 순서를 보장합니다.
Akka/Orleans 같은 Actor 프레임워크의 Supervision 계층이나 위치 투명성 없이, **Per-Key 직렬화에 집중**한 경량 구현입니다.

- `INodeActorManager` — `ConcurrentDictionary<long, INodeActor>`로 유저별 큐 관리
- `QueuedResponseWriter<T>` — Lock-free 생산자-소비자 패턴
- `ActorDisposeQueue` — 큐 해제 시 자기 교착(self-deadlock) 방지

---

## Service Mesh RPC 흐름

### 요청 → 응답 4단계

```
[1단계: 요청 전송]
NodeSender.RequestAsync(dest, msgId, payload)
  → RequestCache에 RequestKey 등록
  → NodeCommunicator.Send(packet)
  → NetMQ Router 소켓으로 전송

[2단계: 수신 및 처리]
대상 NodeCommunicator.OnProcessPacket
  → NodePacketRouter: IsReply==0이므로 요청으로 분류
  → NodeEventHandler.ProcessPacket
  → INodeDispatcher → [NodeController] 핸들러 실행

[3단계: 응답 전송]
핸들러에서 context.Reply(response)
  → NodeCommunicator.Send(reply packet, IsReply=1)

[4단계: 응답 수신 및 완료]
요청자 NodePacketRouter: IsReply==1
  → RequestCache.TryReply(requestKey, packet)
  → TaskCompletionSource 완료 → 호출자에게 결과 반환
```

### 노드 등록 및 탐색

- 각 노드는 시작 시 **Redis Registry**에 자신의 NodeId, 서버 타입, 주소를 등록
- 12비트 동적 Node ID 생성
- 다른 노드는 Registry를 조회하여 NetMQ Router-Router 연결 수립
- Full Mesh 토폴로지: 모든 노드가 모든 노드와 직접 연결

---

## 성능 최적화

### Zero-Copy 패턴

| 기법 | 설명 |
|------|------|
| `Msg.Move(ref msg)` | NetMQ 메시지의 소유권 이전 — 복사 없이 전달 |
| `Msg.InitPool(size)` | 전용 ArrayPool에서 버퍼 할당 |
| `msg.Slice()` | 포인터 기반 버퍼 슬라이싱 |
| `Span<T>`, `Memory<T>` | Hot path에서 Heap 할당 제거 |
| `stackalloc` | 스택 기반 소규모 버퍼 할당 |
| `DedicatedNetMQBufferPool` | NetMQ 전용 격리된 ArrayPool\<byte\> |

### Headroom 사전 할당

Gateway에서 패킷 재할당 없이 라우팅 헤더를 prepend하기 위해, 패킷 생성 시 헤더 공간을 미리 확보합니다.

```
할당 시: [──Headroom──][──Payload──]
전달 시: [EndPointHdr][──Payload──]   ← 복사 없이 Headroom을 헤더로 사용
```

---

## 사용 기술

| 분류 | 기술 | 사용 목적 |
|------|------|-----------|
| **런타임** | .NET 9.0 | C# 13, Nullable 참조 타입, Primary Constructor |
| **네트워킹** | NetMQ 4.x | ZeroMQ Router-Router — Service Mesh & P2P |
| | NetCoreServer | 비동기 고성능 TCP 서버 |
| **직렬화** | Protocol Buffers | 바이너리 메시지 직렬화 |
| **압축** | K4os.Compression.LZ4 | 고속 페이로드 압축 |
| **데이터 저장** | StackExchange.Redis | SSOT — 세션, 노드 레지스트리, 분산 락 |
| **관측성** | Serilog | 구조화된 로깅 |
| | OpenTelemetry SDK | 분산 추적, 메트릭, 로그 (OTLP) |
| **오케스트레이션** | .NET Aspire | 멀티 프로세스 로컬 개발 환경 |
| **코드 생성** | Roslyn Source Generator | 컴파일 타임 코드 생성 |
| **테스트** | xUnit + Moq + FluentAssertions | 단위/통합 테스트 |

---

## 에러 코드 시스템

계층형 5자리 포맷 (`ABCCC`):

| 범위 | 분류 | 예시 |
|------|------|------|
| `10xxx` | 인증 | `10001` — 유효하지 않은 토큰 |
| `11xxx` | 세션 | `11001` — 세션 미발견 |
| `20xxx` | Gateway | `20001` — 연결 거부 |
| `30xxx` | GameServer | `30001` — 유저 세션 미발견 |
| `40xxx` | Service Mesh | `40001` — RPC 타임아웃 |
