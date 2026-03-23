# Node Service Mesh (노드 서비스 망)

**레이어:** Control Plane
**목적:** 서버 간 제어 및 관리 통신
**상태:** Stateless (RPC 기반)

---

## 목차
1. [개요](#개요)
2. [아키텍처](#아키텍처)
3. [핵심 컴포넌트](#핵심-컴포넌트)
4. [RPC 메커니즘](#rpc-메커니즘)
5. [노드 발견](#노드-발견)

---

## 개요

Node Service Mesh는 **서버 간 제어, 관리, RPC 통신을 담당하는 전용 네트워크 망**입니다.

### 설계 목표
- 🕸️ **Full Mesh 토폴로지**: 모든 노드가 서로 직접 연결 (N × N)
- 🔧 **서버 간 제어**: Session Pin, Disconnect, Reroute 등
- 🔄 **RPC 통신**: 요청-응답 패턴
- 🌐 **서비스 디스커버리**: Redis 기반 노드 발견
- 📊 **모니터링**: Heartbeat 및 상태 관리
- 🔄 **복원력**: 단일 노드 장애 시에도 다른 경로로 우회 가능

### 주요 특징
| 특징 | 설명 |
|------|------|
| **토폴로지** | **Full Mesh** (모든 노드가 서로 연결) |
| **참여자** | Gateway, GameServer, Stateless Services |
| **프로토콜** | ServiceMeshProtocol (RPC) |
| **전송** | NetMQ Router-Router (Full Mesh) |
| **라우팅** | Node ID Routing |
| **상태** | Stateless (요청-응답) |
| **연결성** | Gateway ↔ Gateway ✅, GameServer ↔ GameServer ✅, 모든 조합 가능 |

---

## 아키텍처

### 전체 구조

```
┌─────────────────────────────────────────────────────────┐
│          Node Service Mesh Architecture                 │
└─────────────────────────────────────────────────────────┘

┌──────────┐          ┌──────────────┐          ┌──────────┐
│ Gateway  │◄─────────┤  GameServer  │─────────►│   Mail   │
│          │   RPC    │              │   RPC    │ Service  │
└────┬─────┘          └──────┬───────┘          └──────────┘
     │                       │
     │    Router-Router      │
     │    Mesh (NetMQ)       │
     │                       │
     └───────────┬───────────┘
                 │
                 ▼
         ┌──────────────┐
         │    Redis     │
         │  (Registry)  │
         └──────────────┘
           - Node Info
           - Heartbeat
           - Session SSOT
```

### Full Mesh 토폴로지

```
                    ┌────────────┐
                    │   Redis    │
                    │ (Registry) │
                    └─────┬──────┘
                          │ (노드 등록/발견)
          ┌───────────────┼───────────────┐
          │               │               │
          ▼               ▼               ▼
    ┌──────────┐    ┌──────────┐    ┌──────────┐
    │Gateway#1 │◄──►│Gateway#2 │◄──►│  Mail    │
    │          │    │          │    │ Service  │
    └────┬─────┘    └────┬─────┘    └────┬─────┘
         │ ╲            ╱ │ ╲            ╱ │
         │   ╲        ╱   │   ╲        ╱   │
         │     ╲    ╱     │     ╲    ╱     │
         │       ╲╱       │       ╲╱       │
         │       ╱╲       │       ╱╲       │
         │     ╱    ╲     │     ╱    ╲     │
         │   ╱        ╲   │   ╱        ╲   │
         │ ╱            ╲ │ ╱            ╲ │
         ▼               ▼▼               ▼
    ┌──────────┐    ┌──────────┐    ┌──────────┐
    │  Game    │◄──►│  Game    │◄──►│  Shop    │
    │Server #1 │    │Server #2 │    │ Service  │
    └──────────┘    └──────────┘    └──────────┘
         ▲               ▲               ▲
         └───────────────┴───────────────┘
              (모든 노드가 서로 연결)

Full Mesh 연결 예시:
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Gateway #1   ◄──RPC──►  Gateway #2      ✅
Gateway #1   ◄──RPC──►  GameServer #1   ✅
Gateway #1   ◄──RPC──►  GameServer #2   ✅
Gateway #1   ◄──RPC──►  Mail Service    ✅
Gateway #1   ◄──RPC──►  Shop Service    ✅

GameServer#1 ◄──RPC──►  GameServer #2   ✅
GameServer#1 ◄──RPC──►  Mail Service    ✅
GameServer#1 ◄──RPC──►  Shop Service    ✅

Mail Service ◄──RPC──►  Shop Service    ✅
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

**특징**:
- **진정한 Full Mesh**: 모든 노드 (Gateway, GameServer, Services)가 서로 직접 연결
- **N × N 연결**: N개 노드가 있으면 N×(N-1)/2 개의 연결
- **Gateway ↔ Gateway**: RPC 가능 (예: 라우팅 정보 공유)
- **GameServer ↔ GameServer**: RPC 가능 (예: 크로스 서버 메시지)
- **Service ↔ Service**: RPC 가능 (예: Mail → Shop 연동)
- **Redis 기반 Discovery**: 노드 등록 및 발견, Heartbeat
- **NetMQ Router-Router**: 각 노드가 RouterSocket으로 양방향 통신
- **Stateless RPC**: 요청-응답 패턴, 상태 없음
- **복원력**: 한 노드 장애 시 다른 경로로 우회 가능

---

## 핵심 컴포넌트

### 1. NodeCommunicator

**위치**: `SimpleNetEngine.Node/Network/NodeCommunicator.cs`

**역할**:
- NetMQ RouterSocket으로 모든 노드와 통신
- 노드 발견 및 연결 관리
- RPC 요청/응답 라우팅

**핵심 코드**:
```csharp
public class NodeCommunicator : IDisposable
{
    private RouterSocket? _router;
    private NetMQPoller? _poller;
    private readonly NodeManager _nodeManager;

    public async Task StartAsync()
    {
        _router = new RouterSocket();

        // Identity 설정
        var identity = $"{_config.ServerType}-{_config.NodeId}";
        _router.Options.Identity = Encoding.UTF8.GetBytes(identity);

        _router.Bind($"tcp://*:{_config.Port}");
        _router.ReceiveReady += OnReceiveReady;

        _poller = new NetMQPoller { _router };
        _poller.RunAsync();

        // Redis에서 다른 노드 발견 및 연결
        await DiscoverAndConnectNodesAsync();
    }

    private async Task DiscoverAndConnectNodesAsync()
    {
        var registry = new RedisClusterRegistry(_redis);
        var otherNodes = await registry.GetAllNodesAsync();

        foreach (var node in otherNodes)
        {
            if (node.NodeId != _config.NodeId)
            {
                ConnectToNode(node);
            }
        }
    }

    public void ConnectToNode(ServerInfo serverInfo)
    {
        var endpoint = $"tcp://{serverInfo.Host}:{serverInfo.Port}";
        _router!.Connect(endpoint);

        var remoteNode = new RemoteNode(serverInfo);
        _nodeManager.AddNode(remoteNode);

        _logger.LogInformation("Connected to node: {NodeId} at {Endpoint}",
            serverInfo.RemoteId, endpoint);
    }

    private void OnReceiveReady(object? sender, NetMQSocketEventArgs e)
    {
        // [Identity][Empty][ServiceMeshHeader][Payload] 수신
        var identity = e.Socket.ReceiveFrameBytes();
        var empty = e.Socket.ReceiveFrameBytes();
        var headerBytes = e.Socket.ReceiveFrameBytes();
        var payload = e.Socket.ReceiveFrameBytes();

        var header = ServiceMeshProtocol.Deserialize(headerBytes);

        // NodeDispatcher로 전달
        _dispatcher.Dispatch(header, payload, identity);
    }
}
```

---

### 2. NodeDispatcher

**위치**: `SimpleNetEngine.Node/Core/NodeDispatcher.cs`

**역할**:
- RPC 요청을 Controller로 라우팅
- 응답 전송
- Reflection 기반 핸들러 등록

**핵심 코드**:
```csharp
public class NodeDispatcher : INodeDispatcher
{
    private readonly Dictionary<ushort, HandlerInfo> _handlers = new();

    public void RegisterControllers(IServiceProvider serviceProvider)
    {
        var controllerTypes = Assembly.GetEntryAssembly()!
            .GetTypes()
            .Where(t => t.GetCustomAttribute<NodeControllerAttribute>() != null);

        foreach (var type in controllerTypes)
        {
            var methods = type.GetMethods()
                .Where(m => m.GetCustomAttribute<PacketHandlerAttribute>() != null);

            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<PacketHandlerAttribute>()!;
                var msgId = attr.MessageId;

                _handlers[msgId] = new HandlerInfo {
                    ControllerType = type,
                    Method = method,
                    ParameterType = method.GetParameters()[0].ParameterType
                };
            }
        }
    }

    public async Task Dispatch(ServiceMeshHeader header, byte[] payload,
                                byte[] senderIdentity)
    {
        if (!_handlers.TryGetValue(header.MsgId, out var handler))
        {
            _logger.LogWarning("No handler for MsgId: {MsgId}", header.MsgId);
            return;
        }

        // Controller 인스턴스 생성 (Scoped)
        using var scope = _serviceProvider.CreateScope();
        var controller = scope.ServiceProvider.GetRequiredService(handler.ControllerType);

        // Protobuf 메시지 역직렬화
        var request = (IMessage)Activator.CreateInstance(handler.ParameterType)!;
        request.MergeFrom(payload);

        // Handler 호출
        var task = (Task<IMessage>)handler.Method.Invoke(controller, new[] { request })!;
        var response = await task;

        // 응답 전송
        if (response != null)
        {
            await SendResponseAsync(senderIdentity, header.SequenceId, response);
        }
    }
}
```

---

### 3. NodeSender (RPC 클라이언트)

**위치**: `SimpleNetEngine.Node/Network/NodeSender.cs`

**역할**:
- RPC 요청 전송
- 응답 대기 (CompletionSource)
- Timeout 처리

**핵심 코드**:
```csharp
public class NodeSender : INodeSender
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<IMessage>> _pending = new();
    private long _sequenceId = 0;

    public async Task<TResponse> SendAsync<TRequest, TResponse>(
        long targetNodeId, TRequest request, int timeoutMs = 5000)
        where TRequest : IMessage
        where TResponse : IMessage, new()
    {
        var sequenceId = Interlocked.Increment(ref _sequenceId);
        var tcs = new TaskCompletionSource<IMessage>();
        _pending[sequenceId] = tcs;

        try
        {
            // ServiceMesh Header 생성
            var header = new ServiceMeshHeader {
                MsgId = GetMsgId<TRequest>(),
                SequenceId = sequenceId,
                SourceNodeId = _config.NodeId,
                TargetNodeId = targetNodeId
            };

            // 전송
            var identityBytes = GetNodeIdentity(targetNodeId);
            _router.SendMoreFrame(identityBytes);      // Identity
            _router.SendMoreFrame(Array.Empty<byte>()); // Empty delimiter
            _router.SendMoreFrame(header.Serialize());  // Header
            _router.SendFrame(request.ToByteArray());   // Payload

            // 응답 대기 (Timeout)
            using var cts = new CancellationTokenSource(timeoutMs);
            var task = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, cts.Token));

            if (task != tcs.Task)
                throw new TimeoutException($"RPC timeout: {typeof(TRequest).Name}");

            return (TResponse)await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(sequenceId, out _);
        }
    }

    public void OnResponseReceived(long sequenceId, IMessage response)
    {
        if (_pending.TryRemove(sequenceId, out var tcs))
        {
            tcs.SetResult(response);
        }
    }
}
```

---

## RPC 메커니즘

### 요청-응답 흐름

```
GameServer                    Gateway
    │                            │
    │──RPC: PinSession──────────►│
    │  MsgId: 101                │
    │  SequenceId: 12345         │
    │  SocketId: abc123          │
    │  SessionId: 67890          │
    │                            │
    │                            │ (Handler 실행)
    │                            │ session.Pin(...)
    │                            │
    │◄──Response─────────────────│
    │  MsgId: 102                │
    │  SequenceId: 12345         │
    │  Success: true             │
    │                            │
  (CompletionSource 완료)
```

### ServiceMeshProtocol

**위치**: `SimpleNetEngine.Protocol/Packets/ServiceMeshProtocol.cs`

**구조**:
```
┌────────────────────────────────────────┐
│       ServiceMeshHeader                │
├────────────────────────────────────────┤
│  MsgId (ushort)                        │
│  SequenceId (long)                     │
│  SourceNodeId (long)                   │
│  TargetNodeId (long)                   │
│  Timestamp (long)                      │
└────────────────────────────────────────┘
        +
┌────────────────────────────────────────┐
│      Protobuf Payload                  │
│  (Request or Response message)         │
└────────────────────────────────────────┘
```

---

## 노드 발견 및 레지스트리 관리 (Service Registry)

### Redis 기반 Service Registry

**위치**: `SimpleNetEngine.Node/Cluster/RedisClusterRegistry.cs`

**역할**:
- 노드 정보 등록 (Heartbeat)
- 다른 노드 목록 조회
- TTL 관리 및 장애 감지 (Dead Node Eviction)

#### 🔄 동적 노드 ID 생성 (Auto NodeId Generation)
마이크로서비스 환경에서 스케일 아웃(Scale-out) 시 노드 ID 충돌을 방지하고 레지스트리 관리를 최적화하기 위해 **12-bit 동적 고유 노드 ID 생성 전략**을 사용합니다.

1. **Guid 기반 안정성**: 서버가 부팅될 때 `Guid.NewGuid()`를 통해 런타임 내 절대적으로 고유한 `NodeGuid`를 생성합니다.
2. **XxHash64 해싱 & Snowflake 호환성**: `UniqueIdGenerator.ComputeNodeIdFromGuid(NodeGuid)`를 호출하여, 이 Guid를 고품질 해시 함수(XxHash64)를 거쳐 **0~4095 (12-bit)** 범위의 Node ID로 압축합니다. 
3. **Registry 최적화 및 충돌 방지**: 
   - 고정 ID (`GameServer-1` 등)를 사용할 경우, 서버 재시작 시 이전 프로세스가 남긴 세션 찌꺼기나 비정상 종료 시 충돌 문제가 발생할 수 있습니다.
   - 동적 ID를 사용함으로써, 노드가 비정상 종료되어도 지정된 TTL(예: 15초) 이후 Redis Heartbeat가 자동으로 해당 노드 레지스트리를 만료(Evict)시킵니다.
   - 새롭게 띄워진 노드는 완전히 새로운 ID를 발급받으므로 레지스트리 등록 시 좀비 노드와의 상태(State) 꼬임 현상을 원천 차단합니다.

**핵심 코드**:
```csharp
public class RedisClusterRegistry : IClusterRegistry
{
    private const string NodeKeyPrefix = "node:";

    public async Task RegisterAsync(ServerInfo nodeInfo, TimeSpan ttl)
    {
        var key = GetNodeKey(nodeInfo.RemoteId);
        var json = JsonSerializer.Serialize(nodeInfo);

        await _database.StringSetAsync(key, json, ttl);
    }

    public async Task<List<ServerInfo>> GetAllNodesAsync()
    {
        var server = _redis.GetServer(_redis.GetEndPoints().First());
        var keys = server.Keys(pattern: NodeKeyPrefix + "*").ToArray();

        var nodes = new List<ServerInfo>();
        foreach (var key in keys)
        {
            var json = await _database.StringGetAsync(key);
            if (!json.IsNullOrEmpty)
            {
                var node = JsonSerializer.Deserialize<ServerInfo>(json!);
                if (node != null)
                    nodes.Add(node);
            }
        }

        return nodes;
    }

    public async Task HeartbeatAsync(long nodeId, TimeSpan ttl)
    {
        var key = GetNodeKey(nodeId);
        await _database.KeyExpireAsync(key, ttl);
    }
}
```

---

## Controller 예시

### Gateway의 ControlController

**위치**: `GatewayServer/Controllers/ControlController.cs`

```csharp
[NodeController]
public class ControlController
{
    private readonly ConcurrentDictionary<Guid, GatewaySession> _clientSessions;
    private readonly SessionMapper _sessionMapper;

    [PacketHandler(ServiceMeshPinSessionReq.MsgId)]
    public async Task<IMessage> PinSession(ServiceMeshPinSessionReq req)
    {
        var socketId = new Guid(req.SocketId.Span);

        if (_clientSessions.TryGetValue(socketId, out var session))
        {
            session.Pin(req.SourceGameServerId, req.SessionId);
            _sessionMapper.PinSocket(socketId, req.SourceGameServerId, req.SessionId);

            return new ServiceMeshPinSessionRes { Success = true };
        }

        return new ServiceMeshPinSessionRes { Success = false };
    }

    [PacketHandler(ServiceMeshDisconnectClientReq.MsgId)]
    public async Task<IMessage> DisconnectClient(ServiceMeshDisconnectClientReq req)
    {
        var socketId = new Guid(req.SocketId.Span);

        if (_clientSessions.TryGetValue(socketId, out var session))
        {
            session.Disconnect();
            return new ServiceMeshDisconnectClientRes { Success = true };
        }

        return new ServiceMeshDisconnectClientRes { Success = false };
    }
}
```

---

## GameServer의 BFF 패턴

GameServer는 **Backend for Frontend** 역할을 수행하여 Stateless Services를 호출합니다:

```csharp
// GameServer에서 MailService 호출 예시
public class MailController
{
    private readonly INodeSender _nodeSender;

    [PacketHandler(GetMailListReq.MsgId)]
    public async Task<IMessage> GetMailList(GetMailListReq req)
    {
        // Stateless MailService로 RPC 호출
        var response = await _nodeSender.SendAsync<MailServiceGetMailReq, MailServiceGetMailRes>(
            targetNodeId: MailServiceNodeId,
            request: new MailServiceGetMailReq {
                UserId = req.UserId
            }
        );

        // 클라이언트로 응답
        return new GetMailListRes {
            Mails = { response.Mails }
        };
    }
}
```

---

## 관련 문서

- [전체 아키텍처 개요](01-overview.md)
- [Game Session Channel](02-user-packet-mesh.md)
- [라이브러리 구조](04-library-structure.md)
- [타입 시스템](05-type-system.md)
