# 라이브러리 계층 구조

**목적:** 명확한 의존성 계층 구조를 통한 유지보수성 향상
**원칙:** 하위 계층은 상위 계층을 참조할 수 없음

---

## 목차
1. [계층 개요](#계층-개요)
2. [각 계층 상세](#각-계층-상세)
3. [코드 생성기 (Source Generator)](#코드-생성기-source-generator)
4. [의존성 그래프](#의존성-그래프)
5. [네임스페이스 규칙](#네임스페이스-규칙)

---

## 계층 개요

시스템은 **5개의 Tier**로 구성되어 있으며, 각 Tier는 명확한 책임을 가집니다:

```
┌──────────────────────────────────────────────────────┐
│              Tier 4: Sample Applications              │
│        GatewaySample, GameSample, NodeSample         │
│         - 서버 실행 프로젝트 (Program.cs)              │
│         - Aspire AppHost 통합                         │
└────────────────────┬─────────────────────────────────┘
                     │
                     ▼
┌──────────────────────────────────────────────────────┐
│           Tier 3: Server Libraries                    │
│     SimpleNetEngine.Gateway, SimpleNetEngine.Game    │
│         - 서버별 특화 기능 (라이브러리)                 │
│         - DI Extension, Controllers, Middleware       │
└────────────────────┬─────────────────────────────────┘
                     │
                     ▼
┌──────────────────────────────────────────────────────┐
│          Tier 2: Service Mesh Framework               │
│              SimpleNetEngine.Node                     │
│         - Node간 통신 (RPC, Discovery)                │
│         - NodeCommunicator, NodeDispatcher            │
└────────────────────┬─────────────────────────────────┘
                     │
                     ▼
┌──────────────────────────────────────────────────────┐
│         Tier 1: Distributed Infrastructure            │
│           SimpleNetEngine.Infrastructure              │
│         - Discovery, DistributeLock, Telemetry        │
│         - Redis 기반 인프라                            │
└────────────────────┬─────────────────────────────────┘
                     │
                     ▼
┌──────────────────────────────────────────────────────┐
│            Tier 0: Protocol Definitions               │
│            SimpleNetEngine.Protocol                   │
│         - Packets, Memory, Utils                      │
│         - 언어 중립적 프로토콜                          │
└──────────────────────────────────────────────────────┘
```

---

## 각 계층 상세

### Tier 0: SimpleNetEngine.Protocol

**역할**: 순수 프로토콜 정의 및 기본 유틸리티

**포함 내용**:
```
SimpleNetEngine.Protocol/
├── Packets/
│   ├── EndPointHeader.cs        # TCP 엔드포인트 헤더
│   ├── GameHeader.cs            # Game Session Channel 헤더
│   ├── NodeHeader.cs            # Node Service Mesh 헤더
│   ├── NodePacket.cs            # Node 패킷 (Zero-Copy)
│   ├── Packets.cs               # 패킷 정의
│   └── PacketConfig.cs          # 패킷 상수
├── Memory/
│   └── ArrayPoolBufferWriter.cs # 버퍼 풀
└── Utils/
    ├── HashHelper.cs            # 해시
    ├── IpFinder.cs              # IP 검색
    └── UniqueIdGenerator.cs     # ID 생성
```

**의존성**:
- `Google.Protobuf`
- `NetMQ`
- `PacketParserGenerator` (Source Generator, 컴파일 타임 전용)

**네임스페이스**: `SimpleNetEngine.Protocol.*`

---

### Tier 1: SimpleNetEngine.Infrastructure

**역할**: 분산 시스템 인프라 컴포넌트

**포함 내용**:
```
SimpleNetEngine.Infrastructure/
├── DistributeLock/
│   └── RedisDistributeLock.cs   # 분산 락
├── Middleware/
│   └── ...                      # 인프라 미들웨어
├── NetMQ/
│   └── NetMQArrayBufferPool.cs  # NetMQ 메모리 풀
├── Telemetry/
│   └── TelemetryHelper.cs       # OpenTelemetry 헬퍼
└── HostApplicationStopper.cs    # 호스트 종료
```

**의존성**:
- `SimpleNetEngine.Protocol` ✅
- `StackExchange.Redis`
- `NetMQ`
- `OpenTelemetry` (분산 트레이싱)
- `Microsoft.Extensions.Hosting.Abstractions`

**네임스페이스**: `SimpleNetEngine.Infrastructure.*`

---

### Tier 2: SimpleNetEngine.Node

**역할**: Node Service Mesh 구현 (Control Plane)

**포함 내용**:
```
SimpleNetEngine.Node/
├── Core/
│   ├── NodeEventHandler.cs          # 이벤트 핸들러 (abstract base)
│   ├── BaseEventHandlers.cs         # Sequential/Parallel/Actor 서브클래스
│   ├── StatelessEventController.cs  # Stateless Service 구현체
│   ├── INodeDispatcher.cs           # Dispatcher 인터페이스
│   ├── NodeDispatcher.cs            # Source Generator 기반 핸들러 라우팅
│   ├── INodeActor.cs                # Actor 인터페이스
│   ├── INodeActorManager.cs         # Actor 매니저 인터페이스
│   ├── NodeManager.cs               # 원격 노드 관리
│   ├── NodeService.cs               # 노드 생명주기
│   ├── QueuedResponseWriter.cs      # 비동기 큐 Writer
│   └── NodeAttributes.cs            # [NodeController], [NodePacketHandler]
├── Network/
│   ├── NodeCommunicator.cs          # NetMQ Router 소켓 통신
│   ├── NodePacketRouter.cs          # 요청/응답 분기 라우터
│   ├── INodeSender.cs               # RPC 송신 인터페이스
│   ├── NodeSender.cs                # RPC 송신 구현
│   ├── RemoteNode.cs                # 원격 노드
│   └── PacketRouter.cs              # API 라우팅
├── Cluster/
│   └── RedisClusterRegistry.cs      # Redis 기반 노드 레지스트리
├── Extensions/
│   └── NodeClusterExtensions.cs     # AddNode() DI 확장
└── Config/
    └── NodeConfig.cs                # 노드 설정
```

**의존성**:
- `SimpleNetEngine.Protocol` ✅
- `SimpleNetEngine.Infrastructure` ✅
- `PacketParserGenerator` (Source Generator)
- `NetMQ`
- `StackExchange.Redis`

**네임스페이스**: `SimpleNetEngine.Node.*`

**특징**:
- ✅ Service Mesh 완전 구현 (Full Mesh RPC)
- ✅ Source Generator 기반 Zero-Reflection 핸들러 등록
- ✅ 3가지 동시성 모델 (Sequential, Parallel, Actor-Serialized)

---

### Tier 3: SimpleNetEngine.Gateway (라이브러리)

**역할**: Gateway Dumb Proxy 기능을 제공하는 **라이브러리**

```
SimpleNetEngine.Gateway/
├── Network/
│   ├── GatewaySession.cs             # 클라이언트 TCP 세션
│   ├── GatewayTcpServer.cs           # NetMQ TCP 서버
│   └── GamePacketRouter.cs           # P2P 패킷 라우터
├── Core/
│   └── GatewayNodeEventHandler.cs    # Node 이벤트 (SequentialNodeEventHandler)
├── Controllers/
│   └── ControlController.cs          # Service Mesh RPC 핸들러
├── Extensions/
│   └── GatewayServiceExtensions.cs   # AddGateway() DI 확장
├── Options/
│   └── GatewayOptions.cs             # Gateway 설정
└── Services/
    └── GatewayHostedService.cs       # Gateway 백그라운드 서비스
```

**네임스페이스**: `SimpleNetEngine.Gateway.*`

### Tier 3: SimpleNetEngine.Game (라이브러리)

**역할**: GameServer Smart Hub/BFF 기능을 제공하는 **라이브러리**

```
SimpleNetEngine.Game/
├── Network/
│   ├── GatewayPacketListener.cs      # Gateway P2P 리스너
│   └── ...
├── Actor/
│   ├── ISessionActor.cs              # 세션 Actor 인터페이스
│   ├── MessageDispatcher.cs          # 클라이언트 패킷 디스패처
│   ├── IUserHandlerRegistrar.cs      # Source Generator 등록 인터페이스
│   └── ...
├── Core/
│   └── GameNodeEventHandler.cs       # Node 이벤트 (SequentialNodeEventHandler)
├── Session/
│   ├── ISessionStore.cs              # 세션 저장소 인터페이스
│   └── RedisSessionStore.cs          # Redis SSOT 구현
├── Middleware/
│   ├── IPacketMiddleware.cs          # Middleware 인터페이스
│   ├── MiddlewarePipeline.cs         # AOP 파이프라인
│   └── ...
├── Controllers/
│   └── ...                           # [UserController] 핸들러
├── Extensions/
│   └── GameServiceExtensions.cs      # AddGame(), AddGameWithNode() DI 확장
└── Options/
    └── GameOptions.cs                # Game 설정
```

**네임스페이스**: `SimpleNetEngine.Game.*`

**Tier 3 공통 의존성**:
- `SimpleNetEngine.Protocol` ✅
- `SimpleNetEngine.Infrastructure` ✅
- `SimpleNetEngine.Node` ✅
- `PacketParserGenerator` (Source Generator)
- `StackExchange.Redis`

---

### Tier 4: Sample Applications (실행 프로젝트)

**역할**: 라이브러리를 사용하는 실제 서버 실행 프로젝트

```
Sample/
├── GatewaySample/          # Gateway 서버 실행
│   └── Program.cs          # AddGateway() 호출
├── GameSample/             # GameServer 실행
│   └── Program.cs          # AddGameWithNode() 호출
├── NodeSample/             # Stateless Node Service 실행
│   └── Program.cs          # AddStatelessNode() 호출
├── Protocol.Sample.User/   # 유저 패킷용 Proto 정의
├── Protocol.Sample.Node/   # 노드 패킷용 Proto 정의
├── TestClient/             # 테스트 클라이언트
└── Sample.AppHost/         # Aspire AppHost (오케스트레이션)
```

---

## 코드 생성기 (Source Generator)

프로젝트는 **2개의 Source Generator 프로젝트**를 사용하여 런타임 Reflection을 완전히 제거했습니다.

### PacketParserGenerator (컴파일 타임)

Proto 정의 및 Controller를 스캔하여 등록 코드를 자동 생성합니다.

| Generator | 입력 | 출력 | 설명 |
|-----------|------|------|------|
| `ParserGenerator` | IMessage 구현 클래스 | `AutoGeneratedInitializer.g.cs` | MsgId 할당 (FNV-1a 32bit) + Parser 등록 |
| `ActorMessageHandlerGenerator` | `[NodeController]` 클래스 | `NodeControllerRegistration.g.cs` | Node RPC 핸들러 DI + 디스패치 코드 |
| `UserControllerGenerator` | `[UserController]` 클래스 | `UserControllerRegistration.g.cs` | User 패킷 핸들러 DI + 디스패치 코드 |
| `ProtoTraceGenerator` | 참조된 Proto 어셈블리 | `ProtoAssemblyLoader.g.cs` | `[ModuleInitializer]`로 Proto 어셈블리 자동 로딩 |

### SimpleNetEngine.ProtoGenerator (런타임)

**역할**: `AutoGeneratedParsers` 정적 클래스를 제공하여, Source Generator가 생성한 코드가 런타임에 사용하는 파서 레지스트리.

```csharp
// Source Generator가 생성한 코드에서 호출:
AutoGeneratedParsers.Register(msgId, parser, fullName, packageName);

// 런타임에서 조회:
AutoGeneratedParsers.GetParserById(msgId);
AutoGeneratedParsers.GetNameById(msgId);
AutoGeneratedParsers.GetIdByInstance(message);
```

### MsgId 할당 방식

```
MsgId = FNV-1a_32bit(FullyQualifiedName) & 0x7FFFFFFF
```

- 컴파일 타임에 `const int MsgId`로 각 Proto 클래스에 생성
- 어셈블리 간 충돌 방지 (namespace 포함 전체 이름 사용)
- `[ModuleInitializer]`로 어셈블리 로드 시 자동 등록

### Proto 어셈블리 자동 로딩 문제와 해결

**문제**: C# 컴파일러는 `const` 값을 인라인하므로, Proto 어셈블리의 `MsgId` 상수만 사용하면 CLR이 해당 어셈블리를 로드하지 않아 `[ModuleInitializer]`가 실행되지 않음.

**해결**: `ProtoTraceGenerator`가 각 프로젝트에 `[ModuleInitializer]`를 생성하여 참조된 Proto 어셈블리를 강제 로딩:
```csharp
// 자동 생성 코드 (ProtoAssemblyLoader.g.cs)
[ModuleInitializer]
internal static void LoadProtoAssemblies()
{
    RuntimeHelpers.RunModuleConstructor(typeof(SomeProtoType).Module.ModuleHandle);
}
```

---

## 의존성 그래프

```
                    ┌─────────────┐  ┌────────────┐  ┌────────────┐
                    │GatewaySample│  │ GameSample │  │ NodeSample │
                    └──────┬──────┘  └─────┬──────┘  └─────┬──────┘
                           │               │               │
              ┌────────────┴───────────────┴───────────────┘
              │                            │
              ▼                            ▼
  ┌────────────────────┐     ┌────────────────────┐
  │SimpleNetEngine     │     │SimpleNetEngine     │
  │  .Gateway          │     │  .Game             │
  └─────────┬──────────┘     └─────────┬──────────┘
            │                          │
            └────────────┬─────────────┘
                         ▼
              ┌────────────────────┐
              │SimpleNetEngine     │
              │  .Node             │
              └─────────┬──────────┘
                        │
                        ▼
              ┌────────────────────┐
              │SimpleNetEngine     │
              │  .Infrastructure   │
              └─────────┬──────────┘
                        │
                        ▼
              ┌────────────────────┐     ┌─────────────────────┐
              │SimpleNetEngine     │     │PacketParserGenerator │
              │  .Protocol         │     │ (Source Generator)   │
              └────────────────────┘     └─────────────────────┘
                                                    │
                                         컴파일 타임 참조로
                                         모든 프로젝트에 적용
```

### 의존성 규칙

1. ✅ **상위 → 하위만 참조 가능** (Tier 4 → 3 → 2 → 1 → 0)
2. ❌ **하위 → 상위 참조 금지** (Protocol은 Node를 참조할 수 없음)
3. ✅ **동일 Tier 간 참조 금지** (Gateway와 Game은 서로 참조 안 함)
4. ✅ **Source Generator는 컴파일 타임 전용** (런타임 의존성 아님)

---

## 네임스페이스 규칙

| 계층 | 프로젝트 | 네임스페이스 패턴 | 예시 |
|------|---------|------------------|------|
| Tier 0 | SimpleNetEngine.Protocol | `SimpleNetEngine.Protocol.*` | `SimpleNetEngine.Protocol.Packets` |
| Tier 1 | SimpleNetEngine.Infrastructure | `SimpleNetEngine.Infrastructure.*` | `SimpleNetEngine.Infrastructure.Telemetry` |
| Tier 2 | SimpleNetEngine.Node | `SimpleNetEngine.Node.*` | `SimpleNetEngine.Node.Core` |
| Tier 3 | SimpleNetEngine.Gateway | `SimpleNetEngine.Gateway.*` | `SimpleNetEngine.Gateway.Network` |
| Tier 3 | SimpleNetEngine.Game | `SimpleNetEngine.Game.*` | `SimpleNetEngine.Game.Actor` |
| Tier 4 | GatewaySample 등 | `{ProjectName}` | `GatewaySample` |
| 생성 | PacketParserGenerator | `PacketParserGenerator` | - |
| 런타임 | SimpleNetEngine.ProtoGenerator | `SimpleNetEngine.ProtoGenerator` | `AutoGeneratedParsers` |

---

## 설계 결정 및 이유

### Gateway/Game을 라이브러리로 분리한 이유

초기에는 Gateway/Game이 직접 실행 가능한 애플리케이션이었으나, 다음 이유로 **라이브러리**로 전환:

1. **재사용성**: 여러 Sample 프로젝트에서 공통 라이브러리로 참조
2. **Aspire 통합**: AppHost가 Sample 프로젝트를 오케스트레이션
3. **관심사 분리**: 서버 로직(라이브러리)과 호스팅 설정(Sample)을 분리
4. **DI Extension 패턴**: `AddGateway()`, `AddGameWithNode()` 등 확장 메서드로 깔끔한 등록

### Source Generator 도입 이유

기존 Reflection 기반 Controller 등록을 Source Generator로 전환:

1. **AOT 호환**: NativeAOT 배포 가능
2. **성능**: 런타임 Reflection 오버헤드 제거
3. **컴파일 타임 검증**: 핸들러 시그니처 오류를 빌드 시 발견
4. **Zero-Reflection**: `AddGeneratedNodeControllers()`, `AddGeneratedUserControllers()` 자동 생성

---

## 관련 문서

- [전체 아키텍처 개요](01-overview.md)
- [Node Service Mesh](03-node-service-mesh.md)
- [Node 메시지 핸들러 아키텍처](../NODE_MESSAGE_HANDLER_ARCHITECTURE.md)
- [Node 동시성 모델](../NODE_CONCURRENCY_MODELS.md)

---

## 변경 이력

| 날짜 | 작업 | 작성자 |
|------|------|--------|
| 2024-12 | 초안 작성 | Claude Code |
| 2026-03-12 | 전면 재작성: SimpleNetEngine.* 네임스페이스, Gateway/Game 라이브러리화, Source Generator, ProtoTraceGenerator | Claude Code |
