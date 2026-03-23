# SimpleNetEngine - NetMQ 기반 MSA 게임 서버 아키텍처 설계 문서

## 프로젝트 개요

**목적**: NetMQ(ZeroMQ) 기반으로 MSA(Microservices Architecture) 환경의 고성능 게임 서버를 구현하기 위한 네트워크 엔진

**핵심 철학**: 클라이언트의 복잡도를 최소화하면서도 서버 내부는 유연한 마이크로서비스 구조를 유지

**프로젝트 구조**:
- `SimpleNetEngine.Protocol` (Tier 0) — 프로토콜 정의
- `SimpleNetEngine.Infrastructure` (Tier 1) — 분산 인프라 (Redis, Telemetry)
- `SimpleNetEngine.Node` (Tier 2) — Service Mesh RPC
- `SimpleNetEngine.Gateway` (Tier 3) — Gateway 라이브러리
- `SimpleNetEngine.Game` (Tier 3) — GameServer 라이브러리
- `PacketParserGenerator` — Source Generator (Zero-Reflection)
- `Sample/*` — 실행 프로젝트 (GatewaySample, GameSample, NodeSample)

---

## 🏗️ 네트워크 구조 설계 (이원화)

본 시스템은 트래픽의 성격에 따라 두 가지 네트워크 채널을 완벽하게 분리하여 사용합니다.

### 1. 내부 서버 간 통신 (Service Mesh)

**기술 스택**: NetMQ

**목적**: 노드(Node) 간의 상태 공유 및 내부 제어를 위한 통신망

**동작 방식**:
- 서버끼리 데이터를 주고받거나 상태를 확인할 때 사용
- 기존에 구축된 Service Mesh를 통해 'Node Request' 방식으로 통신
- 인프라 관리를 위한 유연한 내부망

**사용 사례**:
- 서버 간 상태 동기화
- 내부 RPC 호출
- 중복 로그인 처리 (세션 킥아웃)
- 관리자 명령 전파

### 2. 유저 요청 처리 (Direct P2P 연결)

**기술 스택**: TCP Direct P2P (NetMQ Direct 활용)

**목적**: 유저 트래픽의 빠른 처리를 위한 직접 연결

**동작 방식**:
- 유저가 발생시킨 트래픽은 무거운 Service Mesh망을 타지 않음
- Gateway Server와 Game Server 간의 Direct 연결을 통해 즉각 처리
- **시스템 제약(캡슐화)**: 프레임워크 수준에서 고정된 아키텍처로, 개발자가 임의로 변경 불가

**특징**:
- 저지연 (Low Latency) 보장
- Gateway-GameServer 간 1:1 매핑
- 패킷 순서 보장 (TCP 특성 활용)

---

## 🔄 유저 요청 파이프라인 (Data Flow)

Gateway는 유저와 게임 서버 사이에서 트래픽을 중계하고 콜백을 처리하는 **프록시(Proxy)** 역할을 수행합니다.

```
Client → Gateway → Game Server → [Service Mesh] → Stateless Services
                        ↓
                   콜백 처리
                        ↓
Client ← Gateway ← Game Server
```

### 단계별 흐름

1. **요청 수신 (Request)**
   - 유저(Client) → Gateway Server
   - TCP 소켓 연결 유지

2. **다이렉트 포워딩 (Forward)**
   - Gateway Server → Game Server (Direct P2P 연결 사용)
   - 패킷 내용 분석 없이 투명하게 전달

3. **로직 처리 및 콜백 (Callback)**
   - Game Server에서 실제 요청 처리 완료
   - 필요시 Service Mesh를 통해 다른 서비스 호출
   - Game Server → Gateway Server로 결과 전송

4. **응답 반환 (Response)**
   - Gateway Server → 유저(Client)
   - 원래 소켓을 통해 응답 전달

---

## 🎯 서버 역할 정의

### Gateway Server - Dumb Proxy

**핵심 원칙**: I/O 처리와 라우팅만 전담

**주요 책임**:
- 클라이언트 소켓 연결 유지
- 소켓과 Game Server 간의 매핑 관리
- 패킷의 투명한 포워딩 (내용 분석 없음)
- 빠른 I/O 처리 극대화

**하지 않는 것**:
- ❌ 비즈니스 로직 처리
- ❌ 패킷 내용 분석
- ❌ Redis 조회
- ❌ 세션 검증
- ❌ 라우팅 룰 관리

**설계 이유**:
- **무상태(Stateless) 지향**: `[Client Socket ID <-> Game Server ID]` 매핑만 메모리에 유지
- **성능 극대화**: Redis 응답 지연이 I/O 스레드를 블로킹하는 것을 원천 차단
- **스케일 아웃 용이**: 가벼운 상태 유지로 수평 확장 쉬움

### Game Server - Backend For Frontend (BFF)

**핵심 원칙**: 클라이언트와 내부 서비스를 연결하는 허브(Hub)

**주요 책임**:
- 유저 세션 관리 및 검증
- Redis(SSOT) 조회를 통한 세션 상태 확인
- 패킷 분석 및 라우팅 결정
- Stateful 로직 직접 처리 (전투, 이동 등)
- Service Mesh를 통한 내부 서비스 호출
- 콜백 수신 및 클라이언트 응답 생성

**처리 흐름**:

```csharp
// 의사 코드
async Task HandleClientPacket(Packet packet)
{
    // 1. 패킷 분석
    var opcode = packet.GetOpcode();

    // 2. 라우팅 결정
    if (IsStatefulOperation(opcode))
    {
        // 직접 처리 (전투, 이동 등)
        var result = await ProcessStatefulLogic(packet);
        await SendToClient(result);
    }
    else if (IsStatelessQuery(opcode))
    {
        // Service Mesh를 통해 Stateless 서비스 호출
        var result = await CallStatelessService(packet);
        await SendToClient(result);
    }
}
```

**설계 이유**:
- **중앙 인증**: 모든 외부 트래픽이 하나의 관문을 거침
- **내부 신뢰망**: Game Server가 보내는 Service Mesh 요청은 이미 인증된 것으로 간주
- **로직 중앙 제어**: Command와 Query가 하나의 세션을 통해 통제되어 동기화 이슈 방지

### Stateless Services

**주요 책임**:
- 순수 비즈니스 로직 처리
- 상태 없는 데이터 조회 (Query)
- 독립적인 기능 제공

**인증 처리**:
- Game Server를 거쳐온 요청은 신뢰
- 별도의 JWT 토큰 검증이나 Redis 조회 불필요
- 비즈니스 로직에만 집중

---

## 🔐 세션 관리 및 인증 전략

### Single Source of Truth (SSOT): Redis

**저장 정보**:
- 유저 ID → 현재 접속 중인 Game Server ID
- 세션 토큰 정보
- 접속 상태 (로그인, 게임 중, 로비 등)

### 세션 검증 주체: Game Server

**결정 이유**:

1. **Gateway의 역할 축소 및 성능 극대화**
   - I/O 집중: Gateway는 소켓 연결 유지와 패킷 포워딩에만 집중
   - Redis 의존성 제거: Gateway가 Redis를 직접 조회하지 않음
   - 무상태 지향: 스케일 아웃에 유리

2. **자연스러운 인증 및 세션 고정 플로우**

```
[최초 접속 시]
Client → Gateway → Game Server (임의 할당)
                        ↓
                  Redis 조회 (세션 검증)
                        ↓
                  세션 고정 결정
                        ↓
                  Gateway에게 라우팅 정보 전달
                        ↓
[이후 모든 요청]
Client → Gateway → 고정된 Game Server
```

3. **Service Mesh와의 시너지**
   - 중복 로그인 감지 시, Game Server 간 직접 통신으로 기존 세션 킥아웃
   - Gateway는 복잡한 과정을 몰라도 됨

### 중복 로그인 처리 프로세스

```
[시나리오: 유저가 이미 Game Server A에 접속 중인데, 다른 기기로 접속 시도]

1. 신규 접속 → Gateway → Game Server B (임의 할당)
2. Game Server B가 Redis 조회
   └─> 유저가 이미 Game Server A에 접속 중임을 확인
3. Game Server B → Service Mesh → Game Server A
   └─> "해당 유저 킥아웃 요청"
4. Game Server A가 기존 세션 정리 및 응답
5. Game Server B가 새 세션 등록 (Redis 업데이트)
6. Gateway에게 라우팅 정보 전달
7. 정상 접속 완료
```

---

## 🌐 Gateway 다중화 및 유저 세션 직렬화

### 세션 고정 (Session Pinning)

Gateway 서버는 소켓에 연결된 유저의 Game Server를 고정시킵니다.

```
[매핑 테이블 - Gateway 메모리]
Socket ID  →  Game Server ID
─────────────────────────────
Socket_001 →  GameServer_A
Socket_002 →  GameServer_A
Socket_003 →  GameServer_B
```

### Gateway와 Game Server 다중화 시 고려사항

**문제**: 같은 유저의 요청이 여러 Gateway를 통해 들어올 경우

**해결책**: 세션 직렬화 (Session Serialization)

```
[아키텍처]
Client → Load Balancer (L4/L7)
              ↓
    [Gateway Cluster]
     Gateway A, B, C
              ↓
    고정된 Game Server
```

**구현 방식**:
1. **로드 밸런서 레벨**: Source IP 기반 Sticky Session
2. **Gateway 레벨**: 최초 접속 시 유저 → Game Server 매핑 결정
3. **Game Server 레벨**: Redis에 유저 → Game Server 매핑 저장

**보장 사항**:
- 같은 유저의 모든 요청은 동일한 Game Server로 라우팅
- 패킷 순서 보장 (TCP 특성)
- Race Condition 방지

---

## 🔀 Stateless 서비스 통합 전략

### 설계 질문: 라우팅 주체를 누가 할 것인가?

#### 방안 1: Gateway Routing Mesh (❌ 채택 안 함)

**개념**:
- 클라이언트가 목적에 따라 패킷을 만듦
- Gateway가 패킷을 분석하여 Stateless 서비스로 직접 라우팅

**장점**:
- Game Server 부하 감소
- CQRS의 Query 부분 독립 가능

**치명적 단점**:
- Gateway 복잡도 증가 (패킷 분석 필요)
- 인증/인가 중복 (Stateless 서비스도 세션 검증 필요)
- 패킷 순서 보장 어려움 (Race Condition 가능)
- Gateway의 I/O 처리량 감소

#### 방안 2: Game Server as BFF (✅ 채택)

**개념**:
- 모든 패킷은 Game Server가 수신
- Game Server가 분석 후 필요시 Service Mesh로 Stateless 서비스 호출

**장점**:
- **클라이언트 극단적 단순화**: 단일 TCP 엔드포인트만 사용
- **Gateway 완벽한 캡슐화**: 라우팅 룰 관리 불필요
- **인증 중앙화**: Stateless 서비스는 인증 로직 불필요
- **내부 신뢰망**: Service Mesh 요청은 이미 인증된 것으로 간주
- **로직 중앙 제어**: 동기화 이슈 방지

**트레이드오프**:
- Game Server가 릴레이 역할로 부하 증가
  - 하지만 클라이언트 편의성과 시스템 통일성이 더 중요

### 데이터 흐름 (채택된 방안)

```
[시나리오: 우편함 조회 요청]

1. Client → Gateway
   └─> "우편함 조회" 패킷 전송

2. Gateway → Game Server
   └─> 내용 분석 없이 투명하게 포워딩

3. Game Server에서 패킷 분석
   └─> Opcode 확인: "우편함 조회" (Query)
   └─> Stateless 서비스 호출 결정

4. Game Server → Service Mesh → Mail Service
   └─> Node Request 방식으로 RPC 호출
   └─> 유저 인증 정보 포함 (이미 검증됨)

5. Mail Service
   └─> DB에서 우편함 데이터 조회
   └─> 비즈니스 로직 처리 (읽음 처리 등)

6. Mail Service → Service Mesh → Game Server
   └─> 결과 콜백

7. Game Server → Gateway → Client
   └─> 클라이언트 형식으로 패킷 재포장
   └─> 최종 응답 전송
```

---

## 🚫 투트랙 네트워크 방식 (미채택)

### 개념

**Track 1**: TCP Gateway → Game Server (Stateful 연결)
**Track 2**: HTTP/gRPC Load Balancer → Stateless Web Services (Query)

### 미채택 이유

1. **클라이언트 로직 복잡도 증가**
   - 두 가지 프로토콜(TCP + HTTP) 관리 필요
   - 상태 동기화 복잡
   - 에러 처리 로직 기하급수적 증가

2. **프레임워크 취지와 불일치**
   - "TCP 통신으로 MSA를 쉽게 구현"이 목표
   - 클라이언트에게 복잡성을 전가하면 안 됨

3. **대안**
   - Game Server가 BFF 역할을 수행하면 동일한 효과
   - 클라이언트는 단일 TCP 엔드포인트만 사용
   - 내부적으로는 Service Mesh로 유연하게 확장

---

## 📊 아키텍처 다이어그램

### 전체 시스템 구조

```
┌─────────────────────────────────────────────────────────────────┐
│                        Client Layer                              │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐                      │
│  │ Unity    │  │ Unreal   │  │ Web      │                      │
│  │ Client   │  │ Client   │  │ Client   │                      │
│  └────┬─────┘  └────┬─────┘  └────┬─────┘                      │
│       │             │             │                              │
│       └─────────────┴─────────────┘                              │
│                     │                                            │
│                     │ TCP Connection                             │
│                     ▼                                            │
└─────────────────────────────────────────────────────────────────┘
                      │
┌─────────────────────┼─────────────────────────────────────────┐
│                     ▼         Gateway Layer                    │
│  ┌──────────────────────────────────────────────────┐         │
│  │           L4/L7 Load Balancer                    │         │
│  └──────────────────┬───────────────────────────────┘         │
│                     │                                          │
│       ┌─────────────┼─────────────┐                           │
│       ▼             ▼             ▼                           │
│  ┌─────────┐  ┌─────────┐  ┌─────────┐                       │
│  │Gateway A│  │Gateway B│  │Gateway C│                       │
│  │(Dumb    │  │(Dumb    │  │(Dumb    │                       │
│  │ Proxy)  │  │ Proxy)  │  │ Proxy)  │                       │
│  └────┬────┘  └────┬────┘  └────┬────┘                       │
│       │            │            │                              │
│       │ Direct P2P │            │                              │
│       └────────────┼────────────┘                              │
└────────────────────┼───────────────────────────────────────────┘
                     │
┌────────────────────┼───────────────────────────────────────────┐
│                    ▼         Game Server Layer                 │
│       ┌────────────┬────────────┐                              │
│       ▼            ▼            ▼                              │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐                       │
│  │  Game    │ │  Game    │ │  Game    │                       │
│  │Server A  │ │Server B  │ │Server C  │                       │
│  │  (BFF)   │ │  (BFF)   │ │  (BFF)   │                       │
│  └────┬─────┘ └────┬─────┘ └────┬─────┘                       │
│       │            │            │                              │
│       │     Service Mesh (NetMQ)│                              │
│       └────────────┼────────────┘                              │
└────────────────────┼───────────────────────────────────────────┘
                     │
┌────────────────────┼───────────────────────────────────────────┐
│                    ▼      Stateless Service Layer              │
│       ┌────────────┴────────────┐                              │
│       ▼            ▼            ▼                              │
│  ┌─────────┐ ┌─────────┐ ┌─────────┐                          │
│  │  Mail   │ │  Shop   │ │  Rank   │                          │
│  │ Service │ │ Service │ │ Service │                          │
│  └─────────┘ └─────────┘ └─────────┘                          │
│                                                                 │
│  ┌─────────┐ ┌─────────┐ ┌─────────┐                          │
│  │ Inventory│ │ Social  │ │  ...   │                          │
│  │ Service │ │ Service │ │         │                          │
│  └─────────┘ └─────────┘ └─────────┘                          │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│                      Data Layer                                 │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐                     │
│  │  Redis   │  │ Database │  │  Cache   │                     │
│  │  (SSOT)  │  │ Cluster  │  │  Layer   │                     │
│  └──────────┘  └──────────┘  └──────────┘                     │
└─────────────────────────────────────────────────────────────────┘
```

### 패킷 흐름 상세

```
┌──────────┐                                    ┌──────────────┐
│          │  1. Login Packet (TCP)             │              │
│  Client  │───────────────────────────────────>│   Gateway    │
│          │                                    │  (Dumb Proxy)│
└──────────┘                                    └──────┬───────┘
                                                       │
                                                       │ 2. Forward
                                                       │    (No Analysis)
                                                       ▼
                                               ┌───────────────┐
                                               │  Game Server  │
                                               │     (BFF)     │
                                               └───────┬───────┘
                                                       │
                                    3. Redis Query     │
                                       (Session Check) │
                                                       ▼
                                               ┌──────────────┐
                                               │    Redis     │
                                               │    (SSOT)    │
                                               └──────┬───────┘
                                                       │
                                    4. Session Status  │
                                                       ▼
                                               ┌───────────────┐
                                               │  Game Server  │
                                               │               │
                    ┌──────────────────────────┤ 5. Decision:  │
                    │                          │ - New Session │
                    │                          │ - Kick Old    │
                    │                          └───────┬───────┘
                    │                                  │
     6. Service Mesh│                       7. Update  │
        (If Needed) │                          Redis   │
                    ▼                                  ▼
            ┌────────────┐                    ┌──────────────┐
            │   Other    │                    │    Redis     │
            │Game Server │                    │   (Update)   │
            │            │                    └──────────────┘
            └────────────┘
                    │
        8. Callback │
                    │
                    ▼
            ┌───────────────┐
            │  Game Server  │
            │               │
            └───────┬───────┘
                    │
         9. Response│
                    ▼
            ┌──────────────┐        10. Forward        ┌──────────┐
            │   Gateway    │───────────────────────────>│  Client  │
            └──────────────┘                            └──────────┘
```

---

## 🛡️ 예외 상황 처리

### Game Server 다운 시

**감지**:
- Gateway가 Game Server와의 연결 끊김 감지
- Health Check 실패

**처리**:
1. 해당 Game Server로 매핑된 모든 소켓 연결 종료
2. 클라이언트에게 재접속 요청 메시지 전송
3. Redis에서 해당 Game Server의 세션 정보 정리 (TTL 활용)

**재접속**:
- 클라이언트가 재접속 시 새로운 Game Server로 할당
- Redis의 이전 세션 정보 확인 후 복구 또는 새 세션 생성

### Gateway 다운 시

**처리**:
- L4/L7 Load Balancer가 헬스 체크로 감지
- 해당 Gateway로 향하는 트래픽을 다른 Gateway로 재분배
- 클라이언트는 자동으로 재접속 (TCP 연결 끊김)

**세션 복구**:
- Redis에 저장된 세션 정보를 기반으로 Game Server 재매핑

### Redis 장애 시

**단기 장애** (수 초):
- Game Server에서 요청 재시도 (Retry with Exponential Backoff)
- 클라이언트에게 일시적 오류 응답

**장기 장애**:
- 읽기: Read Replica 또는 로컬 캐시 활용
- 쓰기: 쓰기 큐에 저장 후 복구 시 배치 처리
- 신규 접속: 임시로 로컬 세션으로 처리, 복구 후 동기화

### Service Mesh 통신 실패

**타임아웃**:
- 기본 타임아웃: 5초
- 재시도: 최대 3회 (Exponential Backoff)

**서비스 다운**:
- Circuit Breaker 패턴 적용
- Fallback 응답 제공 또는 캐시된 데이터 사용
- 클라이언트에게 적절한 에러 메시지 전송

---

## 🔧 기술 스택 상세

### Gateway Server (SimpleNetEngine.Gateway)
- **언어**: C# / .NET 9.0
- **네트워크**: NetMQ Router-Router (TCP + Service Mesh 모두)
- **메모리**: GatewaySession 객체 내 라우팅 정보 (Lock-Free O(1))
- **DI**: `AddGateway()` / `AddGatewayWithNode()` 확장 메서드

### Game Server (SimpleNetEngine.Game)
- **언어**: C# / .NET 9.0
- **네트워크**:
  - Client 통신: NetMQ Router-Router (P2P Direct, Game Session Channel)
  - Service Mesh: NetMQ Router-Router (Node Service Mesh)
- **패킷 처리**: Source Generator 기반 `[UserController]` + AOP Middleware Pipeline
- **직렬화**: Protocol Buffers (FNV-1a MsgId)
- **DI**: `AddGameWithNode()` 확장 메서드

### Stateless Services (SimpleNetEngine.Node)
- **언어**: C# / .NET 9.0
- **통신**: NetMQ (Service Mesh, ParallelNodeEventHandler)
- **패킷 처리**: Source Generator 기반 `[NodeController]`
- **직렬화**: Protocol Buffers

### 인프라
- **세션 저장소**: Redis (SSOT)
- **로깅**: Serilog + OpenTelemetry
- **모니터링**: Aspire Dashboard (분산 트레이싱, 로그, 메트릭)
- **오케스트레이션**: .NET Aspire (AppHost)

---

## 📈 성능 고려사항

### Gateway 최적화
- 비동기 I/O (async/await)
- Zero-copy 버퍼 사용
- Connection pooling
- 메모리 풀 활용 (RecyclableMemoryStream)

### Game Server 최적화
- Task-based 비동기 처리
- Service Mesh 요청 병렬화 (필요시)
- 로컬 캐싱 (분산 캐시와 조합)
- 패킷 배칭 (필요시)

### Service Mesh 최적화
- 메시지 압축 (LZ4)
- 연결 재사용
- 로드 밸런싱 (라운드 로빈)

---

## 🔮 확장성 고려

### 수평 확장 (Horizontal Scaling)

**Gateway**:
- Stateless 설계로 무제한 확장 가능
- L4/L7 Load Balancer로 트래픽 분배

**Game Server**:
- 각 인스턴스는 독립적으로 동작
- Redis를 통한 세션 공유
- Service Mesh로 서버 간 통신

**Stateless Services**:
- 완전한 무상태 설계
- Auto-scaling 가능 (Kubernetes HPA 등)

### 수직 확장 (Vertical Scaling)

**병목 지점**:
- Gateway: Network I/O
- Game Server: CPU (로직 처리)
- Redis: Memory

**최적화 방향**:
- Gateway: 네트워크 카드 업그레이드, 멀티코어 활용
- Game Server: CPU 코어 수 증가
- Redis: 메모리 증설, Clustering

---

## 📝 구현 가이드

### 1. Gateway 구현 예시

```csharp
public class GameGateway : TcpServer
{
    private readonly ConcurrentDictionary<Guid, string> _socketToGameServer;
    private readonly ConcurrentDictionary<string, TcpClient> _gameServerClients;

    public GameGateway(IPAddress address, int port) : base(address, port)
    {
        _socketToGameServer = new ConcurrentDictionary<Guid, string>();
        _gameServerClients = new ConcurrentDictionary<string, TcpClient>();
    }

    protected override TcpSession CreateSession()
    {
        return new GatewaySession(this);
    }

    public void RegisterGameServer(string serverId, TcpClient client)
    {
        _gameServerClients[serverId] = client;
    }

    public void MapSocketToGameServer(Guid socketId, string gameServerId)
    {
        _socketToGameServer[socketId] = gameServerId;
    }

    public async Task ForwardToGameServer(Guid socketId, byte[] data)
    {
        if (_socketToGameServer.TryGetValue(socketId, out var gameServerId) &&
            _gameServerClients.TryGetValue(gameServerId, out var client))
        {
            await client.SendAsync(data);
        }
    }
}

public class GatewaySession : TcpSession
{
    public GatewaySession(TcpServer server) : base(server) { }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        // 투명하게 포워딩 (내용 분석 없음)
        var gateway = (GameGateway)Server;
        gateway.ForwardToGameServer(Id, buffer.AsSpan((int)offset, (int)size).ToArray());
    }
}
```

### 2. Game Server 구현 예시

```csharp
public class GameServerPacketHandler
{
    private readonly IServiceMeshClient _serviceMesh;
    private readonly IRedisClient _redis;
    private readonly IGatewayClient _gateway;

    public async Task HandlePacket(Guid socketId, Packet packet)
    {
        // 1. 패킷 분석
        var opcode = packet.Opcode;

        // 2. 라우팅 결정
        if (IsLoginPacket(opcode))
        {
            await HandleLogin(socketId, packet);
        }
        else if (IsStatefulOperation(opcode))
        {
            await HandleStatefulOperation(socketId, packet);
        }
        else if (IsStatelessQuery(opcode))
        {
            await HandleStatelessQuery(socketId, packet);
        }
    }

    private async Task HandleLogin(Guid socketId, Packet packet)
    {
        var loginData = packet.Parse<LoginRequest>();

        // Redis에서 세션 확인
        var existingSession = await _redis.GetSessionAsync(loginData.UserId);

        if (existingSession != null && existingSession.GameServerId != this.ServerId)
        {
            // 다른 Game Server에 접속 중 - 킥아웃 요청
            await _serviceMesh.SendAsync(
                existingSession.GameServerId,
                new KickoutRequest { UserId = loginData.UserId }
            );
        }

        // 새 세션 등록
        await _redis.SetSessionAsync(loginData.UserId, this.ServerId);

        // Gateway에게 라우팅 정보 전달
        await _gateway.RegisterRouting(socketId, this.ServerId);

        // 클라이언트에게 응답
        await SendToClient(socketId, new LoginResponse { Success = true });
    }

    private async Task HandleStatelessQuery(Guid socketId, Packet packet)
    {
        // Service Mesh를 통해 Stateless 서비스 호출
        var serviceName = GetServiceName(packet.Opcode);
        var response = await _serviceMesh.RequestAsync(serviceName, packet);

        // 클라이언트에게 응답
        await SendToClient(socketId, response);
    }

    private async Task SendToClient(Guid socketId, IPacket response)
    {
        var data = response.Serialize();
        await _gateway.SendAsync(socketId, data);
    }
}
```

### 3. Service Mesh 통신 예시

```csharp
public class ServiceMeshClient : IServiceMeshClient
{
    private readonly Dictionary<string, RequestSocket> _connections;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<byte[]>> _pendingRequests;

    public async Task<byte[]> RequestAsync(string serviceName, byte[] data, int timeoutMs = 5000)
    {
        var requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<byte[]>();
        _pendingRequests[requestId] = tcs;

        try
        {
            var socket = GetOrCreateSocket(serviceName);

            // 요청 전송
            var envelope = new ServiceMeshEnvelope
            {
                RequestId = requestId,
                Payload = data
            };

            socket.SendFrame(envelope.Serialize());

            // 응답 대기 (타임아웃 포함)
            using var cts = new CancellationTokenSource(timeoutMs);
            var response = await tcs.Task.WaitAsync(cts.Token);

            return response;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Request to {serviceName} timed out");
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private void ReceiveLoop()
    {
        // 별도 스레드에서 응답 수신
        while (true)
        {
            var envelope = ReceiveEnvelope();

            if (_pendingRequests.TryRemove(envelope.RequestId, out var tcs))
            {
                tcs.SetResult(envelope.Payload);
            }
        }
    }
}
```

---

## 🎓 베스트 프랙티스

### 1. Gateway 개발
- 패킷 내용을 절대 분석하지 말 것
- 메모리 사용량을 최소화할 것
- 비동기 I/O를 철저히 활용할 것
- 로깅은 최소한으로 (성능 영향)

### 2. Game Server 개발
- 모든 외부 요청은 인증/검증 후 처리
- Service Mesh 호출 시 타임아웃 설정 필수
- Redis 장애를 대비한 Fallback 로직 구현
- 메모리 누수 방지 (세션 정리)

### 3. Stateless Service 개발
- Service Mesh를 통해 들어온 요청은 신뢰
- 순수 비즈니스 로직에만 집중
- 상태를 절대 메모리에 저장하지 말 것
- DB 연결은 Connection Pool 활용

### 4. 공통
- 모든 네트워크 통신에 재시도 로직 구현
- Circuit Breaker 패턴 적용
- 구조화된 로깅 (Serilog)
- 메트릭 수집 (성능 모니터링)

---

## 🔍 FAQ

### Q1: 왜 HTTP/REST API를 사용하지 않나요?

**A**: 본 프레임워크의 목표는 "TCP 통신으로 MSA를 쉽게 구현"하는 것입니다. HTTP를 추가하면 클라이언트가 두 가지 프로토콜을 관리해야 하고, 상태 동기화가 복잡해집니다. Game Server가 BFF 역할을 하면 클라이언트는 단일 TCP 엔드포인트만 사용하면서도 내부적으로는 유연한 MSA 구조를 유지할 수 있습니다.

### Q2: Game Server가 병목이 되지 않나요?

**A**: Game Server는 실제 로직을 처리하는 것이 아니라, 패킷을 분석하고 적절한 서비스로 라우팅하는 "얇은 레이어"입니다. 실제 무거운 로직은 Stateless Service에서 처리되므로 Game Server의 부하는 크지 않습니다. 또한 Game Server를 수평 확장하면 충분히 대응 가능합니다.

### Q3: WebSocket은 고려하지 않았나요?

**A**: WebSocket도 훌륭한 선택지이지만, 본 프레임워크는 순수 TCP를 사용합니다. 이유는 다음과 같습니다:
- 더 낮은 오버헤드 (헤더가 작음)
- 더 빠른 연결 수립
- 게임 서버에 특화된 최적화 가능
- 브라우저 클라이언트가 아닌 네이티브 게임 클라이언트 대상

필요하다면 Gateway 레이어에 WebSocket Adapter를 추가하여 브라우저 클라이언트도 지원할 수 있습니다.

### Q4: CQRS를 완벽하게 구현하려면?

**A**: 현재 아키텍처도 CQRS의 핵심 원칙을 따르고 있습니다:
- **Command**: Game Server에서 처리 (Stateful)
- **Query**: Stateless Service에서 처리

더 엄격한 CQRS를 원한다면:
1. Event Sourcing 추가 (Command 처리 후 이벤트 발행)
2. Read Model 분리 (별도 데이터베이스)
3. 하지만 복잡도가 크게 증가하므로, 프로젝트 규모에 따라 결정

### Q5: 모니터링은 어떻게 하나요?

**A**: (추후 추가 예정)
- Prometheus + Grafana
- Serilog → Elasticsearch → Kibana (ELK Stack)
- Application Insights (Azure)
- Custom Dashboard

---

## 🚀 로드맵

### Phase 1: 기본 아키텍처 (현재)
- [x] Gateway Server 구현
- [x] Game Server (BFF) 구현
- [x] Service Mesh 통신
- [x] Redis 세션 관리

### Phase 2: 고급 기능
- [ ] Circuit Breaker 패턴
- [ ] Health Check 자동화
- [ ] Auto-scaling 지원
- [ ] 메트릭 수집

### Phase 3: 운영 도구
- [ ] 관리자 대시보드
- [ ] 실시간 모니터링
- [ ] 로그 분석 도구
- [ ] 부하 테스트 도구

### Phase 4: 최적화
- [ ] 패킷 압축
- [ ] 프로토콜 최적화
- [ ] 메모리 풀 고도화
- [ ] 성능 프로파일링

---

## 📚 참고 자료

### 관련 문서
- [README.md](../README.md) - 프로젝트 개요
- [API Reference](./API.md) - API 문서 (작성 예정)
- [Deployment Guide](./DEPLOYMENT.md) - 배포 가이드 (작성 예정)

### 외부 참고
- [NetMQ Documentation](https://netmq.readthedocs.io/)
- [NetCoreServer](https://github.com/chronoxor/NetCoreServer)
- [Protocol Buffers](https://developers.google.com/protocol-buffers)
- [Microservices Patterns](https://microservices.io/patterns/)

---

**문서 버전**: 2.0
**최종 수정일**: 2026-03-12
**작성자**: NetworkEngine Team

---

## 🔐 세션 관리 및 재접속 처리

**상세 명세**: [SESSION_LIFECYCLE.md](./SESSION_LIFECYCLE.md)

본 시스템은 모바일 환경(핸드오버, 네트워크 불안정)을 고려한 3가지 소켓 생성 시나리오를 처리합니다:

1. **최초 로그인**: 기존 접속 정보 없음
2. **중복 로그인**: 다른 기기에서 접속 시도 (기존 세션 Kick-out)
3. **재접속**: 네트워크 끊김 후 복구 (세션 유지)

### 핵심 설계 원칙

#### 세션별 라우팅 관리
- ✅ GatewaySession 객체 내부에 `PinnedGameServerNodeId` 저장
- ✅ Lock-Free O(1) 접근
- ✅ 소켓 종료 시 자동 정리 (메모리 릭 방지)
- ❌ 싱글톤 Dictionary 사용 금지 (Lock 경합, GC 압박)

#### 멱등성 (Idempotency)
- Sequence ID 기반 중복 요청 검사
- 이미 처리된 요청은 로직 스킵, 결과만 반환
- Lost Ack 상황 대응

#### 분산 환경 처리
- Redis(SSOT)를 통한 세션 상태 공유
- Service Mesh로 서버 간 협력 (Kick-out, Re-routing)
- 스냅샷 동기화 (이벤트 재전송 X)

자세한 구현 명세는 [SESSION_LIFECYCLE.md](./SESSION_LIFECYCLE.md)를 참조하세요.

