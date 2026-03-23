# Node Message Handler Architecture

## 개요

이 문서는 Node Service Mesh의 메시지 핸들러 아키텍처를 설명합니다. 모든 노드 간 RPC 패킷은 `INodeDispatcher` + `[NodeController]` Attribute 기반의 단일 패턴으로 처리됩니다.

---

## 1. 아키텍처 계층 구조

### 1.1 메시지 플로우

```
NodeCommunicator (NetMQ Router Socket)
    ↓ OnProcessPacket Event
NodePacketRouter (이벤트 구독자)
    ↓ 요청/응답 분기
    ├─ IsReply == 1 → RequestCache.TryReply (RPC 응답 처리)
    └─ IsReply == 0 → NodeEventHandler.ProcessPacket (요청 처리)
        ↓ 서브클래스별 동시성 라우팅
        ├─ SequentialNodeEventHandler → SingleThreadEventLoop에 Schedule → ProcessPacketInternalAsync
        └─ ParallelNodeEventHandler → fire-and-forget Task 실행 → ProcessPacketInternalAsync
            ↓
            INodeDispatcher.DispatchAsync
                ↓
                [NodeController] 메서드 실행
                    ↓ 응답 반환
                INodeResponser.Response (응답 전송)
```

### 1.2 각 계층의 역할

| 계층 | 책임 | 위치 |
|------|------|------|
| **NodeCommunicator** | NetMQ 소켓 관리, 이벤트 발행 | SimpleNetEngine.Node/Network/NodeCommunicator.cs |
| **NodePacketRouter** | 요청/응답 분기, Handler 호출 | SimpleNetEngine.Node/Network/NodePacketRouter.cs |
| **NodeEventHandler** | 동시성 모델 라우팅 (abstract base) | SimpleNetEngine.Node/Core/NodeEventHandler.cs |
| **SequentialNodeEventHandler** | SingleThreadEventLoop 순차 처리 | SimpleNetEngine.Node/Core/BaseEventHandlers.cs |
| **ParallelNodeEventHandler** | Per-Task fire-and-forget 병렬 처리 | SimpleNetEngine.Node/Core/BaseEventHandlers.cs |
| **INodeDispatcher** | Source Generator 기반 핸들러 호출 (Zero-Reflection) | SimpleNetEngine.Node/Network/NodeDispatcher.cs |
| **[NodeController]** | 비즈니스 로직 구현 | 사용자 애플리케이션 |

---

## 2. 클래스 계층 구조

### 2.1 NodeEventHandler (추상 베이스)

모든 이벤트 핸들러의 최상위 베이스. `ProcessPacket(NodePacket)`만 abstract로 선언합니다.

```csharp
public abstract class NodeEventHandler
{
    protected readonly ILogger _logger;

    public abstract void ProcessPacket(NodePacket packet);
    public virtual void OnLeaveNode(RemoteNode remoteNode) { }
    public virtual void OnJoinNode(RemoteNode remoteNode) { }
}
```

### 2.2 SequentialNodeEventHandler

`SingleThreadEventLoop`(전용 스레드 + custom SynchronizationContext)를 소유하여 모든 패킷을 단일 스레드에서 순차 처리합니다. Node.js 스타일의 async interleaving으로 await 시 이벤트 루프를 블로킹하지 않으면서도 싱글 스레드 보장을 유지합니다.

```csharp
public abstract class SequentialNodeEventHandler : NodeEventHandler, IDisposable
{
    private readonly SingleThreadEventLoop _eventLoop;

    public override void ProcessPacket(NodePacket packet)
    {
        _eventLoop.Schedule(() => _ = ProcessPacketSafeAsync(packet));
    }

    protected abstract Task ProcessPacketInternalAsync(NodePacket packet);
}
```

**구현체**: `GameNodeEventHandler`, `GatewayNodeEventHandler`

### 2.3 ParallelNodeEventHandler

모든 패킷을 독립된 Task로 fire-and-forget 실행하는 순수 병렬 모델입니다. Stateless Service에서 사용하며, 상태가 없으므로 순서 보장이 불필요합니다.

```csharp
public abstract class ParallelNodeEventHandler : NodeEventHandler
{
    public override void ProcessPacket(NodePacket packet)
    {
        _ = ProcessPacketSafeAsync(packet);
    }

    protected abstract Task ProcessPacketInternalAsync(NodePacket packet);
}
```

**구현체**: `StatelessEventController`

---

## 3. 표준 구현 패턴

모든 서버 타입이 동일한 `INodeDispatcher` + `[NodeController]` 패턴을 사용합니다.

### 3.1 GameServer / Gateway (SequentialNodeEventHandler)

```csharp
public class GameNodeEventHandler : SequentialNodeEventHandler
{
    private readonly INodeDispatcher _dispatcher;
    private readonly INodeResponser _responser;
    private readonly IServiceScopeFactory _scopeFactory;

    protected override async Task ProcessPacketInternalAsync(NodePacket packet)
    {
        IMessage? response = null;
        using (packet)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            response = await _dispatcher.DispatchAsync(scope.ServiceProvider, packet);

            if (response != null)
                _responser.Response(packet.Header, response);
        }
    }
}
```

### 3.2 Stateless Service (ParallelNodeEventHandler)

```csharp
public class StatelessEventController : ParallelNodeEventHandler
{
    private readonly INodeDispatcher _dispatcher;
    private readonly INodeResponser _responser;
    private readonly IServiceScopeFactory _scopeFactory;

    protected override async Task ProcessPacketInternalAsync(NodePacket packet)
    {
        IMessage? response = null;
        using (packet)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            response = await _dispatcher.DispatchAsync(scope.ServiceProvider, packet);

            if (response != null)
                _responser.Response(packet.Header, response);
        }
    }
}
```

---

## 4. 개발자 가이드

### 4.1 새로운 NodeController 추가

1. **Controller 클래스 작성**:
```csharp
[NodeController]
public class MyController
{
    [NodePacketHandler(MsgId = 1001)]
    public async Task<IMessage> HandleMyRequest(MyRequest req)
    {
        // 비즈니스 로직
        return new MyResponse { ... };
    }
}
```

2. **DI 등록 (Source Generator 자동 생성)**:
```csharp
services.AddGeneratedNodeControllers(); // Source Generator가 생성한 코드 (Zero-Reflection)
```

3. **끝!** (NodePacketRouter, NodeEventHandler는 인프라가 자동 처리)

> **참고**: `AddGeneratedNodeControllers()`는 `PacketParserGenerator`의 `ActorMessageHandlerGenerator`가 컴파일 타임에 자동 생성합니다. Reflection 없이 직접 호출 코드가 생성됩니다.

---

## 5. 과거 버그 이력

### 5.1 중복 처리 버그 (수정 완료)

**문제**: `GameHostedService`가 `NodeCommunicator.OnProcessPacket`을 직접 구독하여, `NodePacketRouter`와 이중으로 패킷이 처리되었음.

**수정**: `GameHostedService`에서 이벤트 구독을 제거. `NodePacketRouter`가 유일한 구독자로 동작.

**원칙**: `NodeCommunicator.OnProcessPacket` 이벤트는 **NodePacketRouter만** 구독해야 함. 애플리케이션 계층에서 직접 구독 금지.

---

## 6. 결론

- **INodeDispatcher + [NodeController] Attribute**가 유일한 패킷 처리 패턴
- 동시성 모델은 `NodeEventHandler` 서브클래스(Sequential/Parallel)로 선택
- **NodePacketRouter → NodeEventHandler → INodeDispatcher** 계층이 표준 아키텍처
- Legacy 패턴 (NodeMessageHandler, ActorMessage, ActorMessageFactory)은 완전히 제거됨

---

## 변경 이력

| 날짜 | 작업 | 작성자 |
|------|------|--------|
| 2026-03-06 | 초안 작성, 중복 처리 버그 수정 | Claude Code |
| 2026-03-12 | 리팩터링 반영: Legacy 패턴 제거, 클래스 계층 구조 업데이트 | Claude Code |
| 2026-03-23 | ActorNodeEventHandler 제거, ParallelNodeEventHandler 설명 수정, SequentialNodeEventHandler SingleThreadEventLoop 반영 | Claude Code |
