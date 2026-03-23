# Game Session Channel 처리 흐름 (Data Plane)

## 개요

이 문서는 **Game Session Channel (Data Plane)**을 통한 클라이언트 패킷 처리 흐름을 상세히 설명합니다.

### 특징

- **목적**: 클라이언트 게임 데이터 전송 (초저지연)
- **토폴로지**: 1:N Star (Gateway 중심)
- **프로토콜**: NetMQ Router-Router (Direct P2P)
- **라우팅**: Session-Based Routing (Pinned vs Unpinned)
- **패턴**: Dumb Proxy (Gateway) + Smart Hub (GameServer) + Actor Model

### Network Dualism 위치

```
┌─────────────────────────────────────────────────────────┐
│ Network Dualism                                         │
├─────────────────────────────────────────────────────────┤
│ [1] Game Session Channel (Data Plane) ← 이 문서        │
│     - 클라이언트 게임 데이터                              │
│     - Gateway ↔ GameServer                              │
│     - Session-Based Routing                             │
│                                                         │
│ [2] Node Service Mesh (Control Plane)                  │
│     - 서버 간 RPC 통신                                   │
│     - 모든 노드 Full Mesh                                │
│     - docs/architecture/08-node-rpc-message-flow.md     │
└─────────────────────────────────────────────────────────┘
```

---

## 전체 플로우 다이어그램

```
Client                Gateway              GameServer
  │                      │                     │
  │─────(1) TCP Send────→│                     │
  │                      │                     │
  │                      │─(2) NetMQ Router──→│
  │                      │    (GSCHeader +     │
  │                      │     UserPacket)     │
  │                      │                     │
  │                      │                     │─────(3) Middleware Pipeline
  │                      │                     │           ↓
  │                      │                     │      (4) Actor Routing
  │                      │                     │           ↓
  │                      │                     │      (5) Actor.Push()
  │                      │                     │           ↓
  │                      │                     │      (6) Channel Mailbox
  │                      │                     │           ↓
  │                      │                     │      (7) Sequential Processing
  │                      │                     │           ↓
  │                      │                     │      (8) MessageDispatcher
  │                      │                     │           ↓
  │                      │                     │      (9) Controller.Handle()
  │                      │                     │           ↓
  │                      │                     │      (10) Response Created
  │                      │                     │           ↓
  │                      │←──(11) NetMQ Reply─│      (11) SendResponse Callback
  │                      │    (GSCHeader +     │
  │                      │     ResponseData)   │
  │                      │                     │
  │←────(12) TCP Send────│                     │
  │                      │                     │
```

---

## Phase 1: Client → Gateway (TCP 수신)

### 1.1 GatewaySession.OnReceived()

**파일**: `SimpleNetEngine.Gateway/Network/GatewaySession.cs`

**역할**: Dumb Proxy - 패킷 내용을 분석하지 않고 투명하게 전달

```csharp
protected override void OnReceived(byte[] buffer, long offset, long size)
{
    // 1. 입력 검증
    if (size > PacketDefine.MaxPacketSize || size <= 0)
    {
        Disconnect();
        return;
    }

    // 2. Atomic snapshot: Race Condition 방지
    long targetNodeId = Volatile.Read(ref _pinnedGameServerNodeId);
    long sessionId = Volatile.Read(ref _gameSessionId);

    // 3. 안전한 Span 생성
    var packetSpan = new ReadOnlySpan<byte>(buffer, (int)offset, (int)size);

    // 4. GameServer로 포워딩 (Dumb Proxy - 패킷 내용 분석 안 함)
    _packetRouter.ForwardToGameServer(SocketId, packetSpan, targetNodeId, sessionId);
}
```

**핵심 포인트**:
- **Dumb Proxy 원칙**: Opcode 파싱 없음, 투명한 전달만
- **Session-Based Routing**: `_pinnedGameServerNodeId` 기반 라우팅
- **Zero-Copy**: `ReadOnlySpan<byte>` 직접 전달

### 1.2 GamePacketRouter.ForwardToGameServer()

**파일**: `SimpleNetEngine.Gateway/Network/GamePacketRouter.cs`

**역할**: Gateway의 NetMQ Router - GameServer로 패킷 전송

```csharp
public void ForwardToGameServer(Guid socketId, ReadOnlySpan<byte> userPacket, long pinnedNodeId, long sessionId)
{
    long targetNodeId;

    if (pinnedNodeId > 0)
    {
        // 고정된 GameServer로 전송
        targetNodeId = pinnedNodeId;
    }
    else
    {
        // 미고정: 라운드 로빈으로 GameServer 선택
        var cache = _gameServerNodeIdCache;
        if (cache.Length == 0) return;

        var index = (uint)Interlocked.Increment(ref _roundRobinIndex) % (uint)cache.Length;
        targetNodeId = cache[index];
    }

    // NetMQ 풀에서 Unmanaged 메모리 블록 할당 (GC 0)
    var msg = new Msg();
    msg.InitPool(GSCHeader.Size + userPacket.Length);

    try
    {
        var span = msg.Slice();

        // GSCHeader 작성
        var header = new GSCHeader
        {
            Type = GscMessageType.ClientPacket,
            GatewayNodeId = _options.GatewayNodeId,
            SourceNodeId = _options.GatewayNodeId,
            SocketId = socketId,
            SessionId = sessionId
        };

        MemoryMarshal.Write(span, in header);
        userPacket.CopyTo(span.Slice(GSCHeader.Size));

        // 비동기 큐에 삽입 (워커 스레드에서 실제 전송)
        var envelope = new GSCMessageEnvelope(targetNodeId, ref msg);
        _sendQueue.Enqueue(envelope);
    }
    catch (Exception ex)
    {
        if (msg.IsInitialised)
            msg.Close();
    }
}
```

**핵심 포인트**:
- **GSCHeader 추가**: 라우팅 메타데이터 (GatewayNodeId, SocketId, SessionId)
- **Zero-Copy**: NetMQ Msg.InitPool() 사용
- **비동기 전송**: NetMQQueue로 워커 스레드에 위임

---

## Phase 2: Gateway → GameServer (NetMQ 전송)

### 2.1 GameSessionChannelListener.OnReceiveReady()

**파일**: `SimpleNetEngine.Game/Network/GameSessionChannelListener.cs`

**역할**: GameServer의 NetMQ Router - Gateway로부터 패킷 수신

```csharp
private void OnReceiveReady(object? sender, NetMQSocketEventArgs e)
{
    // NetMQ Router 프레임: [Identity][Empty][GSCHeader][ClientPayload]
    var identityMsg = new Msg();
    var emptyMsg = new Msg();
    var headerMsg = new Msg();
    var payloadMsg = new Msg();

    // 초기화
    identityMsg.InitEmpty();
    emptyMsg.InitEmpty();
    headerMsg.InitEmpty();
    payloadMsg.InitEmpty();

    try
    {
        // 프레임 수신
        e.Socket.Receive(ref identityMsg);
        if (!identityMsg.HasMore) return;

        e.Socket.Receive(ref emptyMsg);
        if (!emptyMsg.HasMore) return;

        e.Socket.Receive(ref headerMsg);
        if (!headerMsg.HasMore) return;

        e.Socket.Receive(ref payloadMsg);

        // GSCHeader 파싱
        if (headerMsg.Size < GSCHeader.Size) return;
        var header = MemoryMarshal.Read<GSCHeader>(headerMsg.Slice());

        // 패킷 타입 분기
        if (header.Type == GscMessageType.ClientConnected)
        {
            _packetHandler.HandleClientConnectedAsync(header.SocketId, header.GatewayNodeId, SendResponse);
            return;
        }

        if (header.Type != GscMessageType.ClientPacket) return;

        var payloadSpan = payloadMsg.Slice();

        // Client 패킷 구조: [EndPointHeader][GameHeader][Payload]
        if (payloadSpan.Length < EndPointHeader.Size + GameHeader.Size) return;

        var gameHeader = MemoryMarshal.Read<GameHeader>(payloadSpan.Slice(EndPointHeader.Size));

        // ClientPacketContext 생성
        var context = new ClientPacketContext
        {
            GatewayNodeId = header.GatewayNodeId,
            SocketId = header.SocketId,
            SessionId = header.SessionId,
            SequenceId = gameHeader.SequenceId,
            ClientPayload = payloadSpan.ToArray() // EndPointHeader 포함
        };

        // IClientPacketHandler로 처리 위임 (fire-and-forget)
        _packetHandler.HandlePacketAsync(context, SendResponse);
    }
    finally
    {
        identityMsg.Close();
        emptyMsg.Close();
        headerMsg.Close();
        payloadMsg.Close();
    }
}
```

**핵심 포인트**:
- **NetMQ Router 프레이밍**: [Identity][Empty][Header][Payload]
- **GSCHeader 파싱**: 라우팅 메타데이터 추출
- **Fire-and-Forget**: 비동기 처리 (HandlePacketAsync)

---

## Phase 3: GameServer 내부 처리 (Middleware → Actor → Controller)

### 3.1 GameServerHub.HandlePacketAsync()

**파일**: `SimpleNetEngine.Game/Core/GameServerHub.cs`

**역할**: Middleware Pipeline 진입점

```csharp
public void HandlePacketAsync(ClientPacketContext context, Action<long, Guid, long, byte[]> sendResponse)
{
    // Middleware Pipeline 실행 (비동기)
    Task.Run(async () =>
    {
        try
        {
            await _pipeline.InvokeAsync(context, sendResponse, _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline execution failed");
        }
    });
}
```

### 3.2 PacketHandlerMiddleware.InvokeAsync()

**파일**: `SimpleNetEngine.Game/Middleware/PacketHandlerMiddleware.cs`

**역할**: Session 상태에 따른 라우팅 분기

```csharp
public async Task InvokeAsync(
    ClientPacketContext context,
    Action<long, Guid, long, byte[]> sendResponse,
    CancellationToken cancellationToken)
{
    if (context.SessionId == 0)
    {
        // Unpinned 세션: IPacketProcessor로 직접 처리
        await _packetProcessor.ProcessAsync(context, sendResponse, cancellationToken);
    }
    else
    {
        // Pinned 세션: ActorDispatcher로 Actor에 라우팅
        await _actorDispatcher.TryDispatchAsync(context, sendResponse, cancellationToken);
    }
}
```

**핵심 포인트**:
- **Unpinned Session**: 익명 상태, IPacketProcessor 직접 처리 (예: 로그인)
- **Pinned Session**: 인증 완료, Actor 시스템으로 라우팅

### 3.3 ActorDispatcher.TryDispatchAsync()

**파일**: `SimpleNetEngine.Game/Actor/ActorDispatcher.cs`

**역할**: SessionId 기반 Actor 조회 및 메시지 전달

```csharp
public Task TryDispatchAsync(
    ClientPacketContext context,
    Action<long, Guid, long, byte[]> sendResponse,
    CancellationToken cancellationToken)
{
    var actorResult = _actorManager.GetOrCreateActor(
        context.SessionId,
        context.SocketId,
        context.GatewayNodeId);

    if (!actorResult.Success)
    {
        _logger.LogWarning("Actor not found: SessionId={SessionId}", context.SessionId);
        return Task.CompletedTask;
    }

    // ActorMessage 생성
    var message = new ActorMessage(
        context.GatewayNodeId,
        context.SocketId,
        context.SessionId,
        context.ClientPayload,
        sendResponse);

    // ★ 핵심: Actor mailbox에 메시지 Push (순차 처리 보장)
    actorResult.Value.Push(message);

    return Task.CompletedTask;
}
```

**핵심 포인트**:
- **Actor 조회**: SessionId → Actor 매핑
- **Actor.Push()**: Channel mailbox에 메시지 추가 (순차 처리 시작)

### 3.4 SessionActor.Push() → ConsumeMailboxAsync()

**파일**: `SimpleNetEngine.Game/Actor/SessionActor.cs`

**역할**: Actor Model - 순차적 메시지 처리

```csharp
// Push: 메시지를 mailbox에 추가 (non-blocking)
public void Push(IActorMessage message)
{
    if (!_mailbox.Writer.TryWrite(message))
    {
        _logger.LogWarning("Failed to push message to Actor mailbox");
    }
}

// ConsumeMailboxAsync: 백그라운드 루프에서 순차 처리
private async Task ConsumeMailboxAsync()
{
    try
    {
        var reader = _mailbox.Reader;

        do
        {
            while (reader.TryRead(out var message))
            {
                await ProcessMessageAsync(message);
            }
        } while (await reader.WaitToReadAsync());
    }
    catch (OperationCanceledException)
    {
        // 정상 종료
    }
}

// ProcessMessageAsync: 개별 메시지 처리
private async Task ProcessMessageAsync(IActorMessage message)
{
    try
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        // IMessageDispatcher로 Controller 호출
        var response = await _dispatcher.DispatchAsync(scope.ServiceProvider, this, message);

        // 응답이 있으면 SendResponse 콜백 호출
        if (response != null && message.SendResponse != null)
        {
            message.SendResponse(
                message.GatewayNodeId,
                message.SocketId,
                message.SessionId,
                response);
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Actor message processing error");
    }
}
```

**핵심 포인트**:
- **Channel<T> Mailbox**: Thread-safe, lock-free 순차 처리
- **SingleReader = true**: 단일 컨슈머 루프 보장
- **Scoped DI**: 각 메시지 처리 시 새 DI 스코프 생성

### 3.5 MessageDispatcher.DispatchAsync()

**파일**: `SimpleNetEngine.Game/Actor/IMessageDispatcher.cs`

**역할**: Opcode 기반 Controller 핸들러 라우팅

```csharp
public async Task<byte[]?> DispatchAsync(
    IServiceProvider serviceProvider,
    ISessionActor actor,
    IActorMessage message)
{
    // Payload에서 opcode 추출
    var opcode = ExtractOpcode(message.Payload);

    if (opcode.HasValue && _handlers.TryGetValue(opcode.Value, out var handler))
    {
        // RequireActorState 상태 검사
        if (_stateCache.TryGetValue(opcode.Value, out var allowedStates) && allowedStates.Length > 0)
        {
            if (!Array.Exists(allowedStates, s => s == actor.Status))
            {
                // 허가되지 않은 상태: 차단
                return null;
            }
        }

        // Handler 실행
        return await handler(serviceProvider, actor, message.Payload);
    }

    return null;
}

// Opcode 추출: [EndPointHeader(4)][Flags(1)][MsgId(4)]...
private static int? ExtractOpcode(byte[] payload)
{
    if (payload.Length < 13) return null;
    return BitConverter.ToInt32(payload, 5); // offset 5에 MsgId
}
```

**핵심 포인트**:
- **Dictionary<int, Handler>**: Opcode → Handler 매핑
- **RequireActorState**: Actor 상태 기반 접근 제어
- **Reflection 없음**: 빌드 시 등록된 핸들러 직접 호출

### 3.6 Controller Handler 실행

**파일**: `Sample/GameSample/Controllers/EchoController.cs`

**예시**: Echo 패킷 처리

```csharp
[UserController]
public class EchoController(ILogger<EchoController> logger)
{
    [UserPacketHandler(ClientOpCode.Echo)]
    public Task<Response> OnEchoRequest(ISessionActor actor, EchoRequest req)
    {
        logger.LogInformation("Echo: {Message} from UserId={UserId}", req.Message, actor.UserId);

        var response = new EchoResponse { Message = req.Message };
        return Task.FromResult(Response.Success(response));
    }
}
```

**핵심 포인트**:
- **[UserPacketHandler]**: Data Plane 전용 핸들러 (Network Dualism)
- **ISessionActor**: Actor 상태 접근 (UserId, ActorId, State 등)
- **Response**: 성공/실패 구조화된 응답

### 3.7 Handler 등록 (AddUserControllers)

**파일**: `Sample/GameSample/Extensions/ControllerExtensions.cs`

**역할**: [UserController] 스캔 및 MessageDispatcher 등록

```csharp
public static IServiceCollection AddUserControllers(this IServiceCollection services)
{
    var assembly = Assembly.GetCallingAssembly();
    var controllerTypes = assembly.GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract && t.GetCustomAttribute<UserControllerAttribute>() != null)
        .ToList();

    // Controller를 Scoped로 등록
    foreach (var controllerType in controllerTypes)
    {
        services.AddScoped(controllerType);
    }

    // MessageDispatcher에 핸들러 등록
    services.AddSingleton<IMessageDispatcher>(sp =>
    {
        var dispatcher = new MessageDispatcher();
        var logger = sp.GetRequiredService<ILogger<MessageDispatcher>>();

        foreach (var controllerType in controllerTypes)
        {
            RegisterControllerHandlers(dispatcher, controllerType, logger);
        }

        return dispatcher;
    });

    return services;
}

private static void RegisterControllerHandlers(
    MessageDispatcher dispatcher,
    Type controllerType,
    ILogger logger)
{
    var methods = controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => m.GetCustomAttribute<PacketHandlerAttribute>() != null);

    foreach (var method in methods)
    {
        var attr = method.GetCustomAttribute<PacketHandlerAttribute>()!;
        var msgId = (int)attr.MsgId;

        // Protobuf 파서 검증
        var parser = AutoGeneratedParsers.GetParserById(msgId);
        if (parser == null) continue;

        // Handler 래퍼 등록: byte[] -> Controller -> byte[]
        dispatcher.RegisterHandler(msgId, CreateHandlerWrapper(controllerType, method, parser));

        logger.LogInformation("Registered handler: MsgId={MsgId} -> {Controller}.{Method}",
            msgId, controllerType.Name, method.Name);
    }
}

private static Func<IServiceProvider, ISessionActor, byte[], Task<byte[]?>> CreateHandlerWrapper(
    Type controllerType,
    MethodInfo method,
    MessageParser parser)
{
    return async (serviceProvider, actor, payload) =>
    {
        // 1. Payload 파싱: [EndPointHeader][GameHeader][Protobuf]
        var (header, message) = ParseClientPacket(payload, parser);

        // 2. Controller 인스턴스 생성 (Scoped DI)
        var controller = serviceProvider.GetRequiredService(controllerType);

        // 3. Handler 메서드 호출
        var task = (Task<Response>)method.Invoke(controller, [actor, message])!;
        var response = await task;

        // 4. Response -> byte[] 직렬화
        return SerializeResponse(response, context);
    };
}
```

**핵심 포인트**:
- **Reflection 기반 등록**: 빌드 시 1회 스캔
- **Handler 래퍼**: byte[] → Protobuf → Controller → Response → byte[]
- **Scoped DI**: 각 메시지마다 새 Controller 인스턴스

---

## Phase 4: GameServer → Gateway → Client (응답 경로)

### 4.1 GameSessionChannelListener.SendResponse()

**파일**: `SimpleNetEngine.Game/Network/GameSessionChannelListener.cs`

**역할**: GameServer의 응답 전송

```csharp
public void SendResponse(long gatewayNodeId, Guid socketId, long sessionId, byte[] responseData)
{
    try
    {
        // Headroom 예약: GSCHeader + EndPointHeader + responseData
        var totalSize = GSCHeader.Size + EndPointHeader.Size + responseData.Length;

        // NetMQ 풀에서 단일 메모리 블록 할당 (GC 0)
        var msg = new Msg();
        msg.InitPool(totalSize);

        var span = msg.Slice();

        // 1. GSCHeader 기록 (내부 라우팅용)
        var gscHeader = new GSCHeader
        {
            Type = GscMessageType.ServerPacket,
            GatewayNodeId = gatewayNodeId,
            SourceNodeId = _options.GameNodeId,
            SocketId = socketId,
            SessionId = sessionId
        };
        MemoryMarshal.Write(span[..GSCHeader.Size], in gscHeader);

        // 2. EndPointHeader 기록 (Gateway가 클라이언트로 보낼 때 사용)
        var endPointHeader = new EndPointHeader
        {
            TotalLength = EndPointHeader.Size + responseData.Length
        };
        MemoryMarshal.Write(span.Slice(GSCHeader.Size, EndPointHeader.Size), in endPointHeader);

        // 3. Response Data ([GameHeader] + [Payload]) 기록
        responseData.AsSpan().CopyTo(span.Slice(GSCHeader.Size + EndPointHeader.Size));

        // 비동기 큐에 추가
        EnqueuePacket(gatewayNodeId, ref msg);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to send response to Gateway-{NodeId}", gatewayNodeId);
    }
}
```

**핵심 포인트**:
- **Headroom 최적화**: GSCHeader + EndPointHeader를 미리 예약
- **Zero-Copy**: Gateway에서 별도 할당 없이 Slice만으로 전송 가능
- **비동기 전송**: NetMQQueue로 워커 스레드에 위임

### 4.2 GamePacketRouter.OnReceiveReady() (Gateway)

**파일**: `SimpleNetEngine.Gateway/Network/GamePacketRouter.cs`

**역할**: Gateway의 NetMQ Router - GameServer 응답 수신

```csharp
private void OnReceiveReady(object? sender, NetMQSocketEventArgs e)
{
    var identityMsg = new Msg();
    var emptyMsg = new Msg();
    var headerMsg = new Msg();
    var payloadMsg = new Msg();

    // 초기화 및 수신
    identityMsg.InitEmpty();
    emptyMsg.InitEmpty();
    headerMsg.InitEmpty();
    payloadMsg.InitEmpty();

    try
    {
        e.Socket.Receive(ref identityMsg);
        if (!identityMsg.HasMore) return;

        e.Socket.Receive(ref emptyMsg);
        if (!emptyMsg.HasMore) return;

        e.Socket.Receive(ref headerMsg);
        if (!headerMsg.HasMore) return;

        e.Socket.Receive(ref payloadMsg);

        // GSCHeader 파싱
        if (headerMsg.Size < GSCHeader.Size) return;
        var header = MemoryMarshal.Read<GSCHeader>(headerMsg.Slice());

        switch (header.Type)
        {
            case GscMessageType.ServerPacket:
                HandleServerPacket(header, payloadMsg.Slice());
                break;

            default:
                _logger.LogWarning("Unexpected message type from GameServer: {Type}", header.Type);
                break;
        }
    }
    finally
    {
        identityMsg.Close();
        emptyMsg.Close();
        headerMsg.Close();
        payloadMsg.Close();
    }
}

private void HandleServerPacket(GSCHeader header, ReadOnlySpan<byte> payload)
{
    // GameServer → Gateway: 클라이언트 응답 (복사 없이 전달)
    if (_clientSessions.TryGetValue(header.SocketId, out var session))
    {
        session.SendFromGameServer(payload);
    }
    else
    {
        _logger.LogWarning("Client session not found: SocketId={SocketId}", header.SocketId);
    }
}
```

**핵심 포인트**:
- **GSCHeader 기반 라우팅**: SocketId로 클라이언트 세션 조회
- **Zero-Copy**: ReadOnlySpan<byte> 직접 전달

### 4.3 GatewaySession.SendFromGameServer()

**파일**: `SimpleNetEngine.Gateway/Network/GatewaySession.cs`

**역할**: Gateway → Client TCP 응답 전송

```csharp
public void SendFromGameServer(ReadOnlySpan<byte> data)
{
    if (!SendAsync(data))
    {
        _logger.LogError(
            "Failed to send packet to client: SocketId={SocketId}, PacketSize={Size}",
            SocketId, data.Length);
        Disconnect();
    }
}
```

**핵심 포인트**:
- **Dumb Proxy**: 내용 분석 없이 투명하게 전달
- **Zero-Copy**: ReadOnlySpan<byte> 직접 TCP 전송

---

## 핵심 컴포넌트 요약

### 1. Gateway (Dumb Proxy)

| 컴포넌트              | 역할                              | 특징                  |
| --------------------- | --------------------------------- | --------------------- |
| GatewaySession        | 클라이언트 TCP 연결 관리          | Opcode 파싱 없음      |
| GamePacketRouter      | Gateway ↔ GameServer NetMQ 통신   | Session-Based Routing |

**원칙**: 비즈니스 로직 없음, I/O만 수행

### 2. GameServer (Smart Hub / BFF)

| 컴포넌트                     | 역할                              | 특징                        |
| ---------------------------- | --------------------------------- | --------------------------- |
| GameSessionChannelListener   | NetMQ Router (Gateway와 통신)     | GSCHeader 라우팅            |
| GameServerHub                | Middleware Pipeline 진입점        | AOP 패턴                    |
| PacketHandlerMiddleware      | Unpinned/Pinned 라우팅 분기       | Session 상태 기반           |
| ActorDispatcher              | Actor 조회 및 메시지 전달         | SessionId → Actor 매핑      |
| SessionActor                 | Actor Model 구현                  | Channel<T> 순차 처리        |
| MessageDispatcher            | Opcode 기반 Controller 라우팅     | Dictionary<int, Handler>    |
| UserController               | 비즈니스 로직 핸들러              | [UserPacketHandler]         |

**원칙**: 모든 클라이언트 요청의 진입점, 세션 검증 및 라우팅 결정

### 3. Actor System

```
┌─────────────────────────────────────────┐
│ SessionActor (per Session)              │
├─────────────────────────────────────────┤
│ - ActorId (= SessionId)                 │
│ - UserId                                │
│ - GatewayNodeId, SocketId               │
│ - Channel<T> Mailbox (순차 처리)        │
│ - Dictionary<string, object> State      │
└─────────────────────────────────────────┘
         ↓
    Push(message)
         ↓
    Channel Mailbox
         ↓
    ConsumeMailboxAsync() (백그라운드 루프)
         ↓
    ProcessMessageAsync() (순차 처리)
         ↓
    IMessageDispatcher.DispatchAsync()
         ↓
    Controller.Handle()
```

**핵심**:
- **동시성 제어**: 단일 Actor 내에서 메시지는 순차 처리
- **상태 관리**: Actor.State로 세션별 게임 상태 저장
- **격리**: 각 세션은 독립적인 Actor

---

## 성능 최적화 포인트

### 1. Zero-Copy 패턴

**적용 위치**:
- GatewaySession → GamePacketRouter: `ReadOnlySpan<byte>`
- NetMQ 전송: `Msg.InitPool()` (Unmanaged 메모리)
- GamePacketRouter → GatewaySession: `ReadOnlySpan<byte>`

**효과**:
- Heap 할당 최소화 (GC 압력 감소)
- 메모리 복사 최소화 (CPU 절약)

### 2. Headroom 예약

**GameSessionChannelListener.SendResponse()**:
```csharp
// GameServer가 응답 생성 시 GSCHeader + EndPointHeader를 미리 예약
var totalSize = GSCHeader.Size + EndPointHeader.Size + responseData.Length;
```

**효과**:
- Gateway에서 재할당 없이 Slice만으로 전송 가능
- Zero-Copy 체인 유지

### 3. NetMQQueue 비동기 전송

**패턴**:
```csharp
// 워커 스레드에서 빠르게 큐에 추가
_sendQueue.Enqueue(envelope);

// Poller 스레드에서 배치 전송
while (e.Queue.TryDequeue(out var envelope, TimeSpan.Zero))
{
    _router.Send(ref envelope.Payload, false);
}
```

**효과**:
- 워커 스레드 블로킹 최소화
- 배치 처리로 처리량 증가

### 4. Actor Mailbox (Channel<T>)

**설정**:
```csharp
_mailbox = Channel.CreateUnbounded<IActorMessage>(new UnboundedChannelOptions
{
    AllowSynchronousContinuations = false,  // 비동기 컨티뉴에이션
    SingleReader = true,                    // 단일 컨슈머 최적화
    SingleWriter = false                    // 다중 프로듀서
});
```

**효과**:
- Lock-free 구현 (CAS 연산)
- Cache-friendly (Sequential access)

### 5. Dictionary 기반 핸들러 라우팅

**MessageDispatcher**:
```csharp
private readonly Dictionary<int, Handler> _handlers = [];

// O(1) 조회
if (_handlers.TryGetValue(opcode, out var handler))
{
    return await handler(...);
}
```

**효과**:
- Reflection 없음 (빌드 시 1회만)
- Hot path에서 빠른 조회

---

## Network Dualism 비교

### Data Plane vs Control Plane

| 항목                | Data Plane (Game Session Channel)      | Control Plane (Node Service Mesh)       |
| ------------------- | -------------------------------------- | --------------------------------------- |
| **목적**            | 클라이언트 게임 데이터                 | 서버 간 RPC                             |
| **토폴로지**        | 1:N Star (Gateway 중심)                | Full Mesh (모든 노드 직접 연결)         |
| **프로토콜**        | NetMQ Router-Router                    | NetMQ Router-Router                     |
| **참여자**          | Gateway ↔ GameServer                   | Gateway, GameServer, Stateless Services |
| **라우팅**          | Session-Based (Pinned/Unpinned)        | ActorId 기반 (NodeId → ActorId)         |
| **메시지 타입**     | UserPacket ([UserPacketHandler])       | NodePacket ([NodePacketHandler])        |
| **핸들러 경로**     | MessageDispatcher → UserController     | INodeDispatcher → NodeController        |
| **순차 처리**       | Actor Mailbox (Channel<T>)             | QueuedResponseWriter<ActorMessage>      |
| **문서**            | 이 문서 (09-game-session-channel-flow) | 08-node-rpc-message-flow.md             |

### 공통 원칙

- **NetMQ 기반**: 두 계층 모두 NetMQ Router-Router 사용
- **Zero-Copy**: Span<byte>, Msg.InitPool() 적극 활용
- **순차 처리**: 메시지 큐 기반 순차 처리 보장
- **Attribute 기반**: [UserPacketHandler] vs [NodePacketHandler] 분리

### 차이점

**Data Plane (클라이언트 중심)**:
- Gateway가 Dumb Proxy (패킷 분석 없음)
- GameServer가 Smart Hub (모든 검증 수행)
- Session-Based Routing (세션 고정)

**Control Plane (서버 간 RPC)**:
- 모든 노드가 동등 (P2P 통신)
- ActorId 기반 라우팅 (특정 노드의 특정 Actor)
- Request-Response RPC 패턴

---

## 시퀀스 다이어그램 (상세)

```
Client          GatewaySession    GamePacketRouter    GameSessionChannelListener    GameServerHub    PacketHandlerMiddleware    ActorDispatcher    SessionActor    MessageDispatcher    Controller
  │                    │                   │                        │                      │                    │                      │                 │                   │              │
  │──TCP Send─────────→│                   │                        │                      │                    │                      │                 │                   │              │
  │                    │                   │                        │                      │                    │                      │                 │                   │              │
  │                    │─ForwardToGameServer────────→│               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │   NetMQ Router         │                      │                    │                      │                 │                   │              │
  │                    │                   │   (GSCHeader +         │                      │                    │                      │                 │                   │              │
  │                    │                   │    UserPacket)         │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │OnReceiveReady─→│                     │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │──HandlePacketAsync──→│                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │─Middleware Pipeline────────→│             │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │─TryDispatchAsync────→│                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │─Push(message)──→│                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │   Channel Mailbox │              │
  │                    │                   │         │               │                      │                    │                      │                 │        (Queue)    │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │  ConsumeMailboxAsync()              │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │  ProcessMessageAsync()              │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │─DispatchAsync────→│              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │─Handle()────→│
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │←─Response────│
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │←─byte[]───────────│              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │  SendResponse Callback              │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │←──NetMQ Reply─│←SendResponse────────│←───────────────────│←─────────────────────│←────────────────│                   │              │
  │                    │                   │         │               │  (GSCHeader +        │                    │                      │                 │                   │              │
  │                    │                   │         │               │   ResponseData)      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │   HandleServerPacket()      │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │←SendFromGameServer──────────│               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
  │←──TCP Send─────────│                   │         │               │                      │                    │                      │                 │                   │              │
  │                    │                   │         │               │                      │                    │                      │                 │                   │              │
```

---

## 요약

### Data Plane의 핵심 특징

1. **Dumb Proxy (Gateway)**:
   - Opcode 파싱 없음
   - 투명한 전달만 수행
   - Session-Based Routing

2. **Smart Hub (GameServer)**:
   - 모든 클라이언트 요청의 진입점
   - 세션 검증 및 라우팅 결정
   - Actor 시스템으로 순차 처리

3. **Actor Model**:
   - Channel<T> Mailbox로 순차 처리
   - 세션별 상태 격리
   - 동시성 제어 자동화

4. **Zero-Copy 최적화**:
   - ReadOnlySpan<byte> 적극 활용
   - NetMQ Msg.InitPool() 사용
   - Headroom 예약으로 재할당 방지

5. **Network Dualism**:
   - [UserPacketHandler]: Data Plane 전용
   - [NodePacketHandler]: Control Plane 전용
   - 명확한 역할 분리

### 참고 문서

- **Control Plane**: [08-node-rpc-message-flow.md](./08-node-rpc-message-flow.md)
- **Network Dualism**: [01-overview.md](./01-overview.md)
- **Actor Model**: [06-actor-lifecycle-process.md](./06-actor-lifecycle-process.md)
