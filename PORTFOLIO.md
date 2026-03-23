# SimpleNetEngine - TCP 게임 서버 프레임워크

## 프로젝트 개요

**웹 서버처럼 사용할 수 있는 TCP 게임 서버 프레임워크**

ASP.NET의 Controller/Attribute 패턴에서 영감을 받아, TCP 패킷 핸들러를 `[NodeController]`와 `[NodePacketHandler]` 어트리뷰트로 선언적으로 작성할 수 있게 만든 개인 프로젝트입니다. 복잡한 TCP 서버 코드를 웹 개발하듯 작성하는 게 목표였습니다.

- **기술 스택**: .NET 9, C# 13, NetMQ(ZeroMQ), NetCoreServer, Redis, Protobuf, Serilog, OpenTelemetry, .NET Aspire(개발용)
- **GitHub**: [SimpleNetEngine](https://github.com/devkentz/SimpleNetEngine)

---

## 프로젝트 동기

이전 프로젝트에서 게임 서버를 운영하면서 구조적인 한계와 성능 문제를 겪었습니다. 웹 서버에서는 당연하게 쓰이는 DI, 미들웨어 파이프라인, 어트리뷰트 기반 라우팅 같은 패턴이 TCP 서버에는 마땅한 대안이 없어서, 직접 만들기로 했습니다.

---

## 핵심 설계

### 1. 유저 트래픽 / 서버 간 통신 채널 분리

이전 프로젝트에서는 유저 트래픽과 서버 간 제어 메시지가 같은 채널을 공유했습니다. 패킷 종류마다 if-else 분기가 붙었고, 이게 hot path 성능을 깎아먹었습니다. 두 채널을 물리적으로 분리해서 각 수신 루프를 단순하게 만들고, 분기 없는 직통 경로를 확보했습니다.

```
┌─────────────────────────────────────────────┐
│  Game Session Channel (Data Plane)          │
│  Client ↔ Gateway ↔ GameServer              │
│  - NetMQ Router-Router (1:N P2P)            │
│  - Session 기반 라우팅                       │
└─────────────────────────────────────────────┘

┌─────────────────────────────────────────────┐
│  Node Service Mesh (Control Plane)          │
│  Gateway ↔ GameServer ↔ Stateless Services  │
│  - NetMQ Router-Router (Full Mesh)          │
│  - RPC 기반 서버 간 통신                     │
└─────────────────────────────────────────────┘
```

### 2. Gateway/GameServer 역할 분리

Gateway와 GameServer가 각자 맡는 일을 명확히 나눴습니다.

| 서버 | 역할 | 핵심 책임 |
|------|------|-----------|
| **Gateway** | 패킷 중계 | TCP I/O 처리, 패킷 포워딩, 암호화/복호화, 압축/해제 |
| **GameServer** | 게임 로직 처리 | 패킷 파싱, 세션 검증, 비즈니스 로직 수행, Stateless Service 위임 |

Gateway가 I/O와 암호화/압축을 전담하면 GameServer는 게임 로직에만 집중할 수 있습니다. Gateway는 패킷 내용을 파싱하지 않고 그대로 넘기기 때문에, GameServer 쪽을 수정해도 Gateway에 영향이 없습니다.

### 3. Controller 기반 패킷 핸들링

ASP.NET Web API처럼 어트리뷰트만 붙이면 패킷 핸들러가 등록되는 구조입니다.

```csharp
[NodeController]
public class LoginController(ISessionManager sessions, IRedisService redis)
{
    [NodePacketHandler(MessageId.LoginRequest)]
    public async ValueTask HandleLogin(NodePacket packet)
    {
        // 로그인 처리 로직
    }
}
```

Source Generator를 두 군데서 활용합니다.

1. **패킷 핸들러 자동 등록**: 어트리뷰트를 분석해서 라우팅 테이블을 컴파일 타임에 만듭니다. 런타임 리플렉션이 필요 없습니다.
2. **Protobuf MsgId 자동 발급**: Protobuf 메시지의 `partial class`를 활용해 클래스 FullName에 FNV-1a 32bit 해시를 적용한 `MsgId` 상수를 생성합니다. ID를 수동 관리할 필요가 없고, 일반적인 게임 프로젝트 규모(수백~수천 메시지)에서 충돌 확률은 0.02% 이하입니다. 충돌이 생기면 빌드 타임에 컴파일 에러로 잡힙니다.

### 4. 미들웨어 파이프라인 (AOP)

ASP.NET Core의 미들웨어 패턴을 TCP 패킷 처리에 가져왔습니다. 노드 간 통신과 클라이언트 통신 각각 별도 인터페이스로 분리하고, DI 생명주기(Singleton/Scoped/Transient)를 선택해서 등록할 수 있습니다.

| 채널 | 인터페이스 | 등록 API |
|------|-----------|----------|
| 클라이언트 통신 | `IPacketMiddleware` | `AddUserMiddleware<T>(ServiceLifetime)` |
| 노드 간 통신 | `INodeMiddleware` | `AddNodeMiddleware<T>(ServiceLifetime)` |

기본 제공 미들웨어로 예외 처리, SequenceId 검증, slow packet 감지, 로깅 등이 있습니다.

### 5. 선택적 암호화 (Handshake)

TLS 1.3의 키 교환 방식에서 아이디어를 가져왔습니다. 모든 패킷을 암호화하면 오버헤드가 크니까, 민감한 데이터가 담긴 패킷만 골라서 암호화합니다.

1. **비대칭 키 배포**: 서버의 ECDSA P-256 공개키를 클라이언트 빌드에 포함합니다.
2. **Handshake**: 클라이언트 접속 시 ECDH P-256으로 키 교환하고, 서버 서명으로 MITM을 막습니다. 세션마다 일회성 키를 생성해 PFS를 보장합니다.
3. **세션 키 확립**: HKDF로 AES-256-GCM 세션 키를 유도합니다. HKDF는 ECDH 공유 비밀값의 통계적 편향을 제거하고, 용도별 키(AES 키, IV 등)를 분리 파생시킵니다.
4. **선택적 적용**: 로그인처럼 보안이 필요한 패킷에만 암호화 플래그를 걸어 사용합니다.

### 6. Observability (OpenTelemetry)

OTLP(OpenTelemetry Protocol)를 통해 Trace, Metrics, Log를 수집합니다. 개발 환경에서는 .NET Aspire Dashboard로 확인하고, 운영 환경에서는 OTLP 호환 백엔드(Grafana, Jaeger 등)로 전송할 수 있습니다.

여러 서버를 거치는 요청 흐름을 추적하기 위해, 패킷 헤더에 TraceId와 SpanId를 직접 넣었습니다. W3C Trace Context의 TraceId(128bit)를 `long` 2개(`TraceIdHigh`, `TraceIdLow`)로 분할하고, 변환 시 `stackalloc` + `BinaryPrimitives`로 Heap 할당 없이 처리합니다. Gateway → GameServer → Stateless Service를 거치는 요청이 하나의 Trace로 연결되어 end-to-end 추적이 가능합니다.

### 7. Service Discovery & Scale-Out

Redis를 Service Discovery 저장소로 사용합니다.

- 각 서버(Gateway, GameServer, Stateless Service)는 시작 시 Redis에 자신을 등록합니다.
- 주기적인 Heartbeat로 서버 생존 여부를 감시합니다.
- 새 서버가 추가되면 기존 노드들이 자동으로 감지해서 NetMQ 연결을 맺습니다.
- Heartbeat 응답이 없는 서버는 자동 제거되어 장애 전파를 막습니다.
- 운영 중 서버 증설/축소가 재시작 없이 가능합니다.

### 8. 동시성 모델 — 유저별 Channel 기반 직렬화

게임 서버에서 핵심은 같은 유저의 요청은 순서대로, 다른 유저의 요청은 동시에 처리하는 것입니다. 유저마다 독립된 `Channel<T>` 큐를 할당하고, 전용 컨슈머가 순차 소비하는 구조로 풀었습니다.

```
유저 A 패킷 ──→ [Channel A] ──→ 컨슈머 A (순차 처리)
유저 B 패킷 ──→ [Channel B] ──→ 컨슈머 B (순차 처리)  ← A와 병렬
유저 C 패킷 ──→ [Channel C] ──→ 컨슈머 C (순차 처리)  ← A, B와 병렬
```

유저별 큐가 물리적으로 분리되어 있어 lock 없이 순차/병렬 처리를 동시에 달성합니다. 각 메시지 처리 시 DI Scope를 생성해서 미들웨어 파이프라인과 Scoped 서비스(EF Core DbContext 등)를 활용할 수 있습니다.

용도에 따라 세 가지 동시성 모델을 제공합니다.

| 모델 | 클래스 | 동작 방식 |
|------|--------|-----------|
| **Event Loop** | `SequentialNodeEventHandler` | 싱글 스레드 + async interleaving (서버 간 RPC) |
| **Parallel** | `ParallelNodeEventHandler` | ID별 독립 Channel 큐 (Stateless Service) |
| **Per-User Channel** | `SessionActor` | 유저별 Channel 큐 직렬화 (게임 세션 처리) |

### 9. 재접속 (Reconnect)

로그인 성공 시 클라이언트에 ReconnectKey(Guid)를 발급하고, Redis에 역인덱스(`ReconnectKey → UserId`)로 저장합니다. 네트워크가 끊기거나 서버 핸드오버가 발생해도 Grace Period 내에 기존 Actor 상태를 복원해서 게임을 이어갈 수 있습니다.

- **같은 서버에 재접속**: 기존 Actor를 즉시 복원하고 새 ReconnectKey를 재발급합니다.
- **다른 서버에 재접속**: Actor 상태가 남아있는 기존 서버로 Gateway 라우팅을 변경하고, 클라이언트에 재시도 응답을 보냅니다.

---

## 성능 최적화

### 송신/수신 처리 경로 분리

- **수신**: NetMQ Poller 이벤트 루프로 패킷 도착 시 즉시 콜백 처리
- **송신**: `NetMQQueue<T>` 기반 스레드 안전 큐로 Poller 루프에서 독립적으로 소비

하나의 스레드에서 송수신을 번갈아 처리하면 송신이 블로킹될 때 수신도 같이 멈춥니다. 경로를 분리해서 이 문제를 없앴고, 송신 쪽에서는 큐에 쌓인 메시지를 배치로 flush할 수 있어 처리량이 올라갑니다.

### Zero-Copy 패킷 처리

불필요한 메모리 복사를 줄이기 위해 Zero-Copy 기법을 적용했습니다.

- `StructLayout(Pack=1)` + `MemoryMarshal.AsRef<T>`로 헤더를 역직렬화 없이 직접 읽음
- NetMQ `Msg.Move()`로 메시지 소유권을 이전해서 복사 없이 처리
- `ArrayPool<byte>` 기반 버퍼 재사용으로 GC 압력 최소화

### 구조체 기반 헤더 — 역직렬화 제거

모든 패킷 헤더를 `StructLayout(Pack=1)` 고정 레이아웃 구조체로 정의하고, `INetHeader<T>` 인터페이스에 `MemoryMarshal.AsRef<T>`를 공통 제공합니다. 바이트 배열을 구조체로 직접 해석하기 때문에 필드별 파싱이나 객체 생성 없이 한 줄로 헤더에 접근합니다. `GSCHeader`, `NodeHeader`, `EndPointHeader` 등 새 헤더를 추가해도 구조체 정의만 하면 같은 코드 경로로 처리됩니다.

### LoggerMessage Source Generator

Hot path에서 `logger.LogDebug("msg {Value}", value)` 형태로 로깅하면 값 타입에 boxing이 발생하고, 로그 레벨이 꺼져 있어도 문자열 보간이 실행됩니다. `[LoggerMessage]` 어트리뷰트를 쓰면 Source Generator가 `IsEnabled` 체크와 타입별 오버로드를 자동 생성해주기 때문에, 로그 레벨 미달 시 인자 평가 없이 즉시 반환되고 boxing도 없습니다.

---

## 성능 결과

### 테스트 환경

| 구분 | 사양 |
|------|------|
| **서버** | AMD Ryzen 7 9800X3D (8C/16T, 96MB V-Cache), 32GB RAM, Windows 11 Pro |
| **클라이언트** | AMD Ryzen 7 3700X (8C/16T), 64GB RAM, Windows 11 Pro |
| **네트워크** | 동일 LAN (1Gbps) |
| **테스트 도구** | DFrame (분산 부하 테스트 프레임워크) |

### 스트레스 테스트 결과

12byte Echo 패킷으로 전체 왕복 경로(클라이언트 → Gateway → GameServer → Gateway → 클라이언트)의 성능을 측정했습니다.

**테스트 조건**: Worker 3대, Concurrency 500 (총 1,500 동시 세션), Total Request 3,000,000

| 항목 | 수치 |
|------|------|
| **RPS** | **~57,000-58,500** |
| **Avg Latency** | 25-26ms |
| **Median** | 23ms |
| **P90** | 35-37ms |
| **P95** | 47-49ms |
| **Max** | 422-545ms |
| **Error Rate** | 0.00% |

Worker별 상세 지표 (대표 테스트):

| 지표 | Worker 1 | Worker 2 | Worker 3 |
|------|----------|----------|----------|
| Succeed | 1,000,000 | 1,000,000 | 1,000,000 |
| Error | 0 | 0 | 0 |
| Avg(ms) | 25.89 | 25.98 | 25.94 |
| P95(ms) | 51.74 | 44.73 | 45.39 |
| RPS | 19,097 | 19,110 | 19,124 |

15회 연속 테스트에서 RPS 56,700~60,200 범위로 안정적이었고, 에러율은 전 구간 0%였습니다.

### 런타임 메트릭 분석

Grafana + .NET Runtime Metrics 대시보드로 부하 테스트 중 서버 런타임 상태를 모니터링했습니다.

| 메트릭 | 수치 | 비고 |
|--------|------|------|
| **CPU Usage** | ~100-150% | GameServer + Gateway 합산 |
| **GC Pause Time** | ~5-10ms | Gen0 위주, tail latency 영향 최소 |
| **GC Collections** | ~200-300/min | Gen0 위주 수거 |
| **Heap Allocated** | ~100-150 MB/s | Zero-Copy 패턴 적용 후 |
| **Memory Working Set** | ~256-512 MiB | Gateway 기준, 안정적 유지 |
| **ThreadPool Threads** | ~20-80 | 부하에 따라 탄력적 조절 |
| **Lock Contentions** | ~200K/min | NetMQQueue Enqueue 경합 (spinlock 수준, 실측 성능 영향 없음) |

### 성능 향상 원인 분석

1. 유저 트래픽과 서버 간 통신 채널을 분리해서 hot path의 불필요한 if-else 분기를 제거했습니다.
2. 송수신 처리 경로를 Poller(수신)와 NetMQQueue(송신)로 나눠서 상호 블로킹을 없앴습니다.
3. Zero-Copy 패턴으로 불필요한 메모리 복사를 줄였습니다.
4. 구조체 기반 헤더 처리로 역직렬화 오버헤드를 없앴습니다.
5. LoggerMessage Source Generator로 hot path 로깅의 boxing과 문자열 보간 비용을 제거했습니다.
