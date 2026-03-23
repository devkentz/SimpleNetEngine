# Game Session Channel (사용자 패킷 망)

**레이어:** Data Plane
**목적:** 클라이언트와 서버 간 게임 데이터 전송
**상태:** Stateful (Session 기반)

---

## 목차
1. [개요](#개요)
2. [아키텍처](#아키텍처)
3. [핵심 컴포넌트](#핵심-컴포넌트)
4. [통신 프로토콜](#통신-프로토콜)
5. [라우팅 메커니즘](#라우팅-메커니즘)
6. [성능 최적화](#성능-최적화)

---

## 개요

Game Session Channel는 **클라이언트의 게임 패킷을 서버로 전송하는 전용 네트워크 망**입니다.

### 설계 목표
- ⚡ **초저지연**: <1ms 목표 (1:N P2P 직접 연결)
- 🔒 **세션 유지**: 클라이언트 연결 상태 추적
- 🚀 **고성능**: Zero-copy, 버퍼 풀, 비동기 처리
- 📡 **투명한 라우팅**: Gateway가 패킷 내용을 분석하지 않음
- 🌟 **Star Topology**: Gateway 1:N GameServer

### 주요 특징
| 특징 | 설명 |
|------|------|
| **참여자** | Client, Gateway, GameServer |
| **프로토콜** | ExternalPacket (Client-Gateway), P2PProtocol (Gateway-GameServer) |
| **전송** | TCP (Client-Gateway), NetMQ (Gateway-GameServer) |
| **라우팅** | Session-Based Routing |
| **상태** | Stateful (Session ID 유지) |

---

## 아키텍처

### 전체 구조

```
┌─────────────────────────────────────────────────────────┐
│              Game Session Channel Architecture              │
└─────────────────────────────────────────────────────────┘

Client (TCP)          Gateway (1:N P2P)            GameServer
   │                         │                           │
   │  ExternalPacket         │      P2PProtocol          │
   │  (Protobuf)             │      (P2PHeader +         │
   │                         │       Payload)            │
   │                         │                           │
   │─────Connect────────────►│                           │
   │                         │                           │
   │                         │◄──────P2P Mesh───────────►│
   │                         │  (NetMQ RouterSocket)     │
   │                         │                           │
   │────Login Packet────────►│                           │
   │                         │                           │
   │                         │───Forward (Session未固定)─►│
   │                         │                           │
   │                         │◄─Pin Session (RPC, Mesh)──│
   │                         │  SocketId: xxx            │
   │                         │  GameServerId: 1          │
   │                         │  SessionId: 12345         │
   │                         │                           │
   │◄───Login Response──────│◄──Forward───────────────┤
   │                         │                           │
   │                         │                           │
   │────Game Packet─────────►│                           │
   │                         │                           │
   │                         │───Forward (Session고정)───►│
   │                         │  TargetNodeId: 1          │
   │                         │  SessionId: 12345         │
   │                         │                           │
   │◄───Game Response───────│◄──Response───────────────│
```

### 1:N P2P Star Topology

```
              Gateway-1                    Gateway-2
                  │                            │
         ┌────────┼────────┐          ┌────────┼────────┐
         │        │        │          │        │        │
         ▼        ▼        ▼          ▼        ▼        ▼
    GameServer-1  │   GameServer-2   │   GameServer-3  │
                  │                   │                 │
                  └───────────────────┴─────────────────┘
                         (각 Gateway는 모든 GameServer와 P2P 연결)

주요 특징:
┌──────────────┐
│  Gateway-1   │──P2P──► GameServer-1
│              │──P2P──► GameServer-2
│              │──P2P──► GameServer-3
└──────────────┘

┌──────────────┐
│  Gateway-2   │──P2P──► GameServer-1
│              │──P2P──► GameServer-2
│              │──P2P──► GameServer-3
└──────────────┘

Gateway끼리 연결: ❌ (Game Session Channel에서는 연결 안 됨)
GameServer끼리 연결: ❌ (Game Session Channel에서는 연결 안 됨)
```

**특징**:
- **1:N 관계**: 각 Gateway가 모든 GameServer와 P2P 연결
- **Star Topology**: Gateway를 중심으로 한 별 모양 구조
- **Gateway 간 연결 없음**: Game Session Channel에서는 Gateway끼리 직접 통신 안 함
- **GameServer 간 연결 없음**: Game Session Channel에서는 GameServer끼리 직접 통신 안 함
- **단일 홉 전송**: Gateway → GameServer 직접 전송 (라우팅 없음)
- **Redis P2P Discovery**: Redis를 통해 서로를 발견하고 P2P 연결 수립

---

## 핵심 컴포넌트

### 1. Client → Gateway (TCP)

#### GatewaySession
**위치**: `GatewayServer/Network/GatewaySession.cs`

**역할**:
- 클라이언트 TCP 연결 관리
- Session-Based Routing 정보 저장
- Dumb Proxy로 동작 (패킷 내용 분석 안 함)

**핵심 코드**:
```csharp
public class GatewaySession : TcpSession
{
    private long _pinnedGameServerNodeId;  // 고정된 GameServer
    private long _gameSessionId;           // 세션 ID

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        // 1. 패킷 검증 (크기, 경계)
        if (size > PacketDefine.MaxPacketSize || size <= 0) {
            Disconnect();
            return;
        }

        // 2. Atomic snapshot (Race Condition 방지)
        long targetNodeId;
        long sessionId;
        lock (_lock) {
            targetNodeId = _pinnedGameServerNodeId;
            sessionId = _gameSessionId;
        }

        // 3. Unpinned 세션 검증
        if (targetNodeId == 0) {
            _logger.LogWarning("Received packet before session pinned");
            return;
        }

        // 4. GameServer로 포워딩 (Dumb Proxy)
        var packetSpan = new ReadOnlySpan<byte>(buffer, (int)offset, (int)size);
        _packetRouter.ForwardToGameServer(SocketId, packetSpan, targetNodeId, sessionId);
    }

    public void Pin(long gameServerNodeId, long gameSessionId)
    {
        lock (_lock) {
            _pinnedGameServerNodeId = gameServerNodeId;
            _gameSessionId = gameSessionId;
        }
    }
}
```

**특징**:
- **Dumb Proxy 원칙**: 패킷 내용을 분석하지 않음
- **Session-Based Routing**: SocketId → (NodeId, SessionId) 매핑
- **Race Condition 방지**: lock을 사용한 atomic snapshot
- **Zero-copy**: ReadOnlySpan 사용

---

### 2. Gateway ↔ GameServer (Dual-Socket P2P)

Game Session Channel은 **Dual-Socket 패턴**을 사용하여 송신과 수신 경로를 물리적으로 분리합니다.

```
Gateway                                    GameServer
┌──────────────────┐                  ┌──────────────────┐
│  Send Router ────│──── Connect ────►│── Recv Router    │  Port N
│  (Poller thread) │                  │   (NetMQPoller)  │
│                  │                  │                  │
│  Recv Router ◄───│──── Connect ────│── Send Router    │  Port N+1
│  (Poller thread) │                  │   (전용 Thread   │
│                  │                  │    + Channel<T>) │
└──────────────────┘                  └──────────────────┘
```

**Dual-Socket을 사용하는 이유**: 하나의 RouterSocket에서 송수신을 모두 처리하면, 송신이 블로킹될 때 수신도 함께 멈추는 문제가 발생합니다. 송수신 소켓을 분리하면 이 경합을 완전히 제거할 수 있습니다.

#### GamePacketRouter (Gateway 측)
**위치**: `SimpleNetEngine.Gateway/Network/GamePacketRouter.cs`

**역할**:
- 모든 GameServer와 Dual-Socket P2P 연결 관리
- Send Router: Gateway → GameServer 패킷 송신
- Recv Router: GameServer → Gateway 응답 수신

**핵심 코드**:
```csharp
public class GamePacketRouter : IDisposable
{
    private RouterSocket? _sendRouter;   // Gateway → GameServer 송신
    private RouterSocket? _recvRouter;   // GameServer → Gateway 수신
    private NetMQPoller? _poller;
    private NetMQQueue<MeshMessageEnvelope>? _sendQueue;

    // GameServer 노드가 클러스터에 합류하면 Dual-Socket 연결
    public void ConnectToGameServer(long nodeId, string recvEndpoint, string sendEndpoint)
    {
        _sendRouter!.Connect(recvEndpoint);  // GS의 Recv Port에 연결
        _recvRouter!.Connect(sendEndpoint);  // GS의 Send Port에 연결
    }

    public void ForwardToGameServer(long socketId, ReadOnlySpan<byte> clientData,
                                     long pinnedNodeId, long sessionId)
    {
        // GSCHeader 직렬화 + clientData를 Msg에 Zero-Copy 기록
        var msg = new Msg();
        msg.InitPool(GSCHeader.SizeOf + clientData.Length);
        // ... 헤더 직렬화 + payload 복사
        var envelope = new MeshMessageEnvelope(pinnedNodeId, ref msg);
        _sendQueue!.Enqueue(envelope);
    }
}
```

---

#### GameSessionChannelListener (GameServer 측)
**위치**: `SimpleNetEngine.Game/Network/GameSessionChannelListener.cs`

**역할**:
- Recv RouterSocket (Port N): Gateway → GameServer 수신 전용 (NetMQPoller 이벤트 기반)
- Send RouterSocket (Port N+1): GameServer → Gateway 송신 전용 (전용 Thread + Channel<T>)

**핵심 코드**:
```csharp
public class GameSessionChannelListener : IDisposable
{
    private RouterSocket? _recvRouter;   // Port N: 수신 전용
    private RouterSocket? _sendRouter;   // Port N+1: 송신 전용
    private NetMQPoller? _recvPoller;    // Recv: 이벤트 기반 수신
    private Thread? _sendThread;         // Send: 전용 스레드

    // Channel<T>: Actor 스레드 → Send 스레드 간 lock-free 큐잉
    private readonly Channel<MeshMessageEnvelope> _sendChannel =
        Channel.CreateUnbounded<MeshMessageEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,    // Send IO 스레드만 읽기
            SingleWriter = false,   // 여러 Actor 스레드에서 쓰기
        });

    public int BoundRecvPort { get; private set; }  // Port N
    public int BoundSendPort { get; private set; }  // Port N+1
}
```

**특징**:
- **Dual-Socket**: 송수신 경로 물리적 분리로 경합 제거
- **Channel<T>**: lock-free 큐잉 (Lock Contention 감소)
- **Zero-Copy**: NetMQ `Msg.Move()` 소유권 이전
- **Router-Router 패턴**: Identity 기반 라우팅

---

## 통신 프로토콜

### 1. ExternalPacket (Client ↔ Gateway)

**위치**: `SimpleNetEngine.Protocol/Packets/Packet.cs`

**구조**:
```
┌────────────────────────────────────────┐
│           ExternalPacket               │
├────────────────────────────────────────┤
│  Header (고정 크기)                     │
│  ├─ PacketId (ushort)                  │
│  ├─ TotalSize (ushort)                 │
│  ├─ PayloadSize (ushort)               │
│  └─ Flags (byte)                       │
├────────────────────────────────────────┤
│  Payload (가변 크기, Protobuf)          │
│  └─ 게임 로직 메시지                    │
└────────────────────────────────────────┘
```

### 2. P2PProtocol (Gateway ↔ GameServer)

**위치**: `SimpleNetEngine.Protocol/Packets/P2PProtocol.cs`

**구조**:
```
┌────────────────────────────────────────┐
│            P2PHeader                   │
├────────────────────────────────────────┤
│  Type (MessageType)                    │
│  GatewayNodeId (long)                  │
│  SourceNodeId (long)                   │
│  SocketId (Guid, 16 bytes)             │
│  SessionId (long)                      │
│  SequenceId (long)                     │
└────────────────────────────────────────┘
        +
┌────────────────────────────────────────┐
│          Client Payload                │
│  (ExternalPacket 전체 또는 응답 데이터)  │
└────────────────────────────────────────┘
```

**MessageType**:
- `ClientPacket`: Gateway → GameServer (클라이언트 요청)
- `ServerPacket`: GameServer → Gateway (서버 응답)

---

## 라우팅 메커니즘

### Session-Based Routing

```
Step 1: 클라이언트 연결
┌─────────┐
│ Client  │─────TCP Connect─────►┌──────────┐
└─────────┘                       │ Gateway  │
                                  └──────────┘
                                  SocketId: abc123
                                  PinnedNodeId: 0 (미고정)
                                  SessionId: 0

Step 2: 로그인 요청 (미고정 상태)
┌─────────┐
│ Client  │──Login Packet───────►┌──────────┐
└─────────┘                       │ Gateway  │
                                  └─────┬────┘
                                        │ (Round Robin)
                                        │ TargetNodeId = 1
                                        ▼
                                  ┌──────────┐
                                  │GameServer│
                                  │   #1     │
                                  └──────────┘

Step 3: Session Pin (Service Mesh RPC)
                                  ┌──────────┐
                                  │GameServer│
                                  │   #1     │
                                  └─────┬────┘
                                        │ RPC: PinSession
                                        │ (SocketId: abc123,
                                        │  NodeId: 1,
                                        │  SessionId: 12345)
                                        ▼
                                  ┌──────────┐
                                  │ Gateway  │
                                  └──────────┘
                                  SocketId: abc123
                                  PinnedNodeId: 1 (고정!)
                                  SessionId: 12345

Step 4: 이후 패킷 (고정 상태)
┌─────────┐
│ Client  │──Game Packet────────►┌──────────┐
└─────────┘                       │ Gateway  │
                                  └─────┬────┘
                                        │ (Session-Based)
                                        │ TargetNodeId = 1
                                        ▼
                                  ┌──────────┐
                                  │GameServer│
                                  │   #1     │
                                  └──────────┘
```

**핵심 로직**:
1. **미고정 세션**: Round Robin으로 GameServer 선택
2. **Session Pin**: GameServer가 RPC로 Gateway에 고정 요청
3. **고정 세션**: SocketId → NodeId 매핑으로 직접 라우팅

---

## 성능 최적화

### 1. Zero-Copy 패턴
```csharp
// ❌ Bad: 불필요한 복사
var data = new byte[size];
Buffer.BlockCopy(buffer, offset, data, 0, size);
ProcessPacket(data);

// ✅ Good: Span 사용
var span = new ReadOnlySpan<byte>(buffer, offset, size);
ProcessPacket(span);
```

### 2. Memory Pool
```csharp
// NetMQ 메모리 풀 사용 (Unmanaged 메모리)
var msg = new Msg();
msg.InitPool(size);  // GC 압력 없음
```

### 3. Async Queue
```csharp
// 워커 쓰레드에서 큐에만 삽입
_sendQueue.Enqueue(envelope);

// Poller 쓰레드에서 일괄 전송 (배칭)
while (queue.TryDequeue(out var msg, TimeSpan.Zero)) {
    _router.Send(ref msg, false);
}
```

### 4. Lock-Free Read
```csharp
// volatile로 lock-free read
private long _pinnedGameServerNodeId;

// Write는 lock 사용
public void Pin(long nodeId) {
    lock (_lock) {
        _pinnedGameServerNodeId = nodeId;
    }
}
```

---

## 관련 문서

- [전체 아키텍처 개요](01-overview.md)
- [Node Service Mesh](03-node-service-mesh.md)
- [라이브러리 구조](04-library-structure.md)
