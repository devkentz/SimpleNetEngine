# SimpleNetEngine 문서

---

## 개요

이 문서는 SimpleNetEngine의 아키텍처, 설계 결정, 그리고 주요 리팩토링 작업을 기록합니다.

시스템은 **두 개의 독립적인 네트워크 망**으로 구성됩니다:
- **Game Session Channel**: 클라이언트와 서버 간 게임 데이터 전송 (Data Plane)
- **Node Service Mesh**: 서버 간 제어 및 관리 통신 (Control Plane)

---

## 문서 구조

### 아키텍처 문서 (`architecture/`)

핵심 아키텍처와 설계 패턴을 설명합니다.

1. **[전체 아키텍처 개요](architecture/01-overview.md)**
   - 두 개의 독립적인 망 (Network Dualism)
   - 핵심 개념 및 설계 원칙
   - 통신 흐름 다이어그램

2. **[Game Session Channel](architecture/02-game-session-channel.md)**
   - 클라이언트 ↔ 서버 게임 패킷 전송
   - Gateway (Dumb Proxy) 구현
   - P2P Full Mesh 토폴로지
   - Session-Based Routing

3. **[Node Service Mesh](architecture/03-node-service-mesh.md)**
   - 서버 간 제어 통신 (RPC)
   - NodeCommunicator, NodeDispatcher 구현
   - Service Registry 패턴
   - BFF (Backend for Frontend) 패턴

4. **[라이브러리 계층 구조](architecture/04-library-structure.md)**
   - Tier 0-4 계층 구조
   - Protocol, Infrastructure, Node, Gateway/Game (라이브러리), Sample (앱)
   - Source Generator (Zero-Reflection)
   - 의존성 그래프 및 규칙

5. **[타입 시스템](architecture/05-type-system.md)**
   - EServerType 통합
   - Service Mesh와 P2P Discovery에서의 사용

6. **[Actor 생명주기](architecture/06-actor-lifecycle-process.md)**
7. **[패킷 구조](architecture/07-packet-structure.md)**
8. **[Node RPC 메시지 플로우](architecture/08-node-rpc-message-flow.md)**
9. **[Game Session Channel 플로우](architecture/09-game-session-channel-flow.md)**

### 주요 기술 문서

- **[Node 메시지 핸들러 아키텍처](NODE_MESSAGE_HANDLER_ARCHITECTURE.md)** — INodeDispatcher + [NodeController] 패턴
- **[Node 동시성 모델](NODE_CONCURRENCY_MODELS.md)** — Sequential/Parallel/Actor 모델
- **[AOP Middleware 패턴](AOP_MIDDLEWARE.md)** — GameServer 패킷 처리 미들웨어
- **[NetMQ 최적화](NETMQ_OPTIMIZATION.md)** — Zero-Copy, 메모리 풀
- **[세션 생명주기](SESSION_LIFECYCLE.md)** — 로그인/재접속/중복 로그인

---

## 빠른 시작 가이드

### 아키텍처 이해하기

1. **처음 읽는 분**: [전체 아키텍처 개요](architecture/01-overview.md) 부터 시작하세요
2. **User Packet 처리**: [Game Session Channel](architecture/02-game-session-channel.md) 참고
3. **서버 간 통신**: [Node Service Mesh](architecture/03-node-service-mesh.md) 참고
4. **코드 구조**: [라이브러리 계층 구조](architecture/04-library-structure.md) 참고

### 개발 가이드

#### 새로운 유저 패킷 핸들러 추가
```csharp
// UserController — Source Generator가 자동 등록 코드 생성
[UserController]
public class YourController
{
    [UserPacketHandler(YourRequest.MsgId)]
    public async Task<IMessage> HandleYourRequest(ISessionActor actor, YourRequest req)
    {
        // 비즈니스 로직
        return new YourResponse { ... };
    }
}
```

#### 새로운 Node RPC 핸들러 추가
```csharp
// NodeController — Source Generator가 자동 등록 코드 생성
[NodeController]
public class YourNodeController
{
    [NodePacketHandler(ServiceMeshYourReq.MsgId)]
    public async Task<IMessage> HandleYourRequest(ServiceMeshYourReq req)
    {
        // RPC 로직
        return new ServiceMeshYourRes { ... };
    }
}
```

> **참고**: `AddGeneratedUserControllers()`, `AddGeneratedNodeControllers()`는 `PacketParserGenerator`가 컴파일 타임에 자동 생성합니다. Reflection 없이 직접 호출 코드가 생성됩니다.

---

## 주요 설계 원칙

### 1. Network Dualism (네트워크 이원성)
- Game Session Channel: 게임 데이터 전송 (초저지연)
- Node Service Mesh: 서버 관리 통신 (일반 지연)

### 2. Dumb Proxy
- 패킷 내용 분석 없이 투명하게 포워딩
- Session ID 기반 라우팅
- 최소한의 메모리 사용

### 3. Smart Hub
- 모든 비즈니스 로직 및 상태 관리
- Session 생명주기 관리
- Stateless Service 호출 (BFF)

### 4. Stateless Services
- Gateway/GameServer와 독립적 동작
- Node Service Mesh 통신만 허용
- 수평 확장 가능

### 5. Zero-Reflection (Source Generator)
- PacketParserGenerator가 컴파일 타임에 핸들러 등록 코드 생성
- MsgId = FNV-1a 32bit hash (어셈블리 간 충돌 방지)
- [ModuleInitializer]로 Proto 어셈블리 자동 로딩

---

## 기술 스택

| 계층 | 기술 |
|------|------|
| **프로토콜** | Google.Protobuf |
| **전송** | NetMQ (ZeroMQ) Router-Router |
| **저장소** | StackExchange.Redis |
| **프레임워크** | .NET 9.0 |
| **코드 생성** | Roslyn Source Generator (PacketParserGenerator) |
| **관측** | OpenTelemetry + Aspire Dashboard |
| **로깅** | Serilog |
| **오케스트레이션** | .NET Aspire (AppHost) |
| **패턴** | Actor Model, RPC, BFF, AOP Middleware |

---

## 용어 사전

| 용어 | 설명 |
|------|------|
| **MsgId** | FNV-1a 32bit hash 기반 메시지 식별자 |
| **Source Generator** | 컴파일 타임 코드 생성 (Zero-Reflection) |

---

## 프로젝트 구조

```
SimpleNetEngine.Protocol (Tier 0)      — 프로토콜 정의
SimpleNetEngine.Infrastructure (Tier 1) — 분산 시스템 인프라
SimpleNetEngine.Node (Tier 2)          — Service Mesh (Control Plane)
SimpleNetEngine.Gateway (Tier 3)       — Gateway 라이브러리
SimpleNetEngine.Game (Tier 3)          — GameServer 라이브러리
PacketParserGenerator                  — Source Generator (컴파일 타임)
SimpleNetEngine.ProtoGenerator         — Proto 런타임 레지스트리
Sample/GatewaySample (App)             — Gateway 실행 프로젝트
Sample/GameSample (App)                — GameServer 실행 프로젝트
Sample/NodeSample (App)                — Stateless Service 실행 프로젝트
Sample/Sample.AppHost (App)            — Aspire 오케스트레이션
```

