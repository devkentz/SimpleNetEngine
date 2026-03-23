# 세션 생명주기 및 소켓 관리 명세서

## 📌 개요

모바일 환경(핸드오버, 네트워크 불안정)을 고려한 분산 게임 서버의 세션 관리 및 재접속 처리 명세

---

## 🔄 소켓 생성 3가지 시나리오

### 1. 최초 로그인 (New Login)

**상황**: 기존 접속 정보가 Redis에 없음

**흐름**:
```
Client → Gateway → Game Server (Round-robin)
                        ↓
                  Redis 조회 (없음)
                        ↓
                  신규 세션 생성
                        ↓
            1. Redis에 매핑 저장 (UserId → NodeId, SessionId)
            2. Gateway에 PinSession 지시
            3. Client에 로그인 성공 응답 (SessionToken 발급)
```

**Game Server 처리**:
```csharp
// 1. Redis 조회
var existingSession = await _redisSessionStore.GetAsync(userId);
if (existingSession == null)
{
    // 2. 신규 세션 생성
    var sessionId = GenerateSessionId();
    var sessionToken = GenerateSessionToken();

    // 3. Redis 저장
    await _redisSessionStore.SetAsync(userId, new SessionInfo
    {
        NodeId = _config.NodeId,
        SessionId = sessionId,
        SessionToken = sessionToken,
        LastSequenceId = 0
    });

    // 4. Gateway에 Pin 지시
    _p2pListener.SendPinSession(gatewayNodeId, socketId, sessionId);

    // 5. 클라이언트 응답
    SendLoginSuccess(sessionToken);
}
```

---

### 2. 중복 로그인 (Duplicate Login)

**상황**: 기존 접속 정보가 있으나 SessionToken이 다름 (다른 기기에서 로그인)

**핵심 원칙**:
- 기존 세션을 안전하게 정리한 후 신규 로그인 진행
- **Game Server A의 저장 완료 콜백을 기다린 후** Game Server B가 처리 (상태 동기화 보장)

**흐름**:
```
Client (새 기기) → Gateway → Game Server B
                                  ↓
                            Redis 조회
                                  ↓
            기존 세션 발견 (Game Server A, 다른 Token)
                                  ↓
         Service Mesh → Game Server A: KickoutRequest
                                  ↓
    [Game Server A]
    1. 클라이언트 연결 해제 (Gateway에 DisconnectClient)
    2. 유저 상태 DB/Redis 저장
    3. 메모리 세션 정리
    4. Service Mesh → Game Server B: KickoutAck
                                  ↓
    [Game Server B]
    KickoutAck 수신 후 신규 로그인 재개
    1. DB에서 최신 데이터 로드
    2. 신규 세션 생성 (새 Token)
    3. Redis 업데이트
    4. Gateway에 PinSession
```

**Game Server B 처리** (중복 로그인 감지):
```csharp
var existingSession = await _redisSessionStore.GetAsync(userId);
if (existingSession != null && existingSession.SessionToken != loginRequest.Token)
{
    // 다른 기기에서 접속 중 - Kick-out 필요
    if (existingSession.NodeId == _config.NodeId)
    {
        // 같은 서버: 직접 처리
        await KickoutLocalSessionAsync(userId);
        await ProceedNewLogin(userId, context);
    }
    else
    {
        // 다른 서버: Service Mesh 요청
        var kickoutRequest = new KickoutRequest
        {
            UserId = userId,
            Reason = "Duplicate login from another device"
        };

        // 동기 대기 (중요!)
        var ack = await _serviceMesh.RequestAsync<KickoutRequest, KickoutAck>(
            existingSession.NodeId,
            kickoutRequest,
            timeout: 5000);

        if (ack.Success)
        {
            // 정리 완료 확인 후 신규 로그인
            await ProceedNewLogin(userId, context);
        }
        else
        {
            // 타임아웃 또는 실패
            SendError("Login failed. Please try again.");
        }
    }
}
```

**Game Server A 처리** (Kick-out 수신):
```csharp
// Service Mesh 핸들러
public async Task<KickoutAck> OnKickoutRequest(KickoutRequest req)
{
    try
    {
        // 1. Gateway에 연결 해제 지시
        _p2pListener.SendDisconnectClient(
            session.GatewayNodeId,
            session.SocketId);

        // 2. 유저 상태 저장
        await SaveUserStateAsync(req.UserId);

        // 3. 메모리 세션 정리
        _sessionManager.Remove(req.UserId);

        // 4. Redis 삭제
        await _redisSessionStore.DeleteAsync(req.UserId);

        return new KickoutAck { Success = true };
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Kickout failed");
        return new KickoutAck { Success = false };
    }
}
```

---

### 3. 재접속 (Reconnect)

**상황**: TCP 연결 불안정 (모바일 핸드오버 등), SessionToken 동일

**핵심 원칙**:
- 기존 세션이 살아있는 Game Server로 라우팅 재조정 (Re-pinning)
- **스냅샷 동기화**: 현재 상태만 전송 (이벤트 히스토리 재전송 X)
- **멱등성 보장**: Sequence ID로 중복 처리 방지

**흐름**:
```
Client (재접속 패킷 + Token) → Gateway → Game Server B
                                              ↓
                                        Redis 조회
                                              ↓
                    기존 세션 발견 (Game Server A, 같은 Token)
                                              ↓
            Gateway에 Re-pinning 지시 (Socket → Game Server A)
                                              ↓
            Gateway: 라우팅 테이블 업데이트
            해당 소켓의 타겟 = Game Server B → A
                                              ↓
            재접속 패킷 재포워딩 → Game Server A
                                              ↓
    [Game Server A]
    1. Sequence ID 비교
    2. 누락된 패킷 확인
    3. 스냅샷 동기화 패킷 전송 (현재 상태)
    4. 재접속 완료
```

**Game Server B 처리** (재접속 감지):
```csharp
var existingSession = await _redisSessionStore.GetAsync(userId);
if (existingSession != null &&
    existingSession.SessionToken == reconnectRequest.Token &&
    existingSession.NodeId != _config.NodeId)
{
    // 기존 세션이 다른 서버에 살아있음
    // → Gateway에 Re-pinning 지시

    _logger.LogInformation(
        "Reconnect: User {UserId} session is on GameServer-{NodeId}. Re-routing...",
        userId, existingSession.NodeId);

    // Gateway에 라우팅 변경 지시
    _p2pListener.SendRerouteCommand(
        context.GatewayNodeId,
        context.SocketId,
        targetNodeId: existingSession.NodeId);

    // 이 서버는 처리 중단 (Game Server A가 처리할 것)
    return;
}
```

**Gateway 처리** (Re-pinning):
```csharp
// GatewaySession 내부
public long PinnedGameServerNodeId { get; private set; }

public void UpdateRouting(long newTargetNodeId)
{
    var oldTarget = PinnedGameServerNodeId;
    PinnedGameServerNodeId = newTargetNodeId;

    _logger.LogInformation(
        "Socket {SocketId} re-routed: GameServer-{Old} → GameServer-{New}",
        Id, oldTarget, newTargetNodeId);
}
```

**Game Server A 처리** (재접속 수신):
```csharp
// 원래 세션을 들고 있던 서버
public async Task HandleReconnect(ReconnectRequest req, ClientPacketContext context)
{
    var session = _sessionManager.Get(req.UserId);
    if (session == null)
    {
        // 세션이 타임아웃되어 정리됨 → 재로그인 요청
        SendError("Session expired. Please login again.");
        return;
    }

    // Sequence ID 비교
    var clientLastSeq = req.LastReceivedSequenceId;
    var serverLastSeq = session.LastSentSequenceId;

    if (clientLastSeq < serverLastSeq)
    {
        // 클라이언트가 일부 패킷을 못 받음
        _logger.LogWarning(
            "Client missing packets: Client={ClientSeq}, Server={ServerSeq}",
            clientLastSeq, serverLastSeq);
    }

    // 스냅샷 동기화 (현재 상태 전송)
    var snapshot = CreateStateSnapshot(session);
    SendResponse(context, snapshot);

    _logger.LogInformation("Reconnect successful: UserId={UserId}", req.UserId);
}

private StateSnapshotPacket CreateStateSnapshot(UserSession session)
{
    return new StateSnapshotPacket
    {
        CurrentHP = session.HP,
        CurrentMP = session.MP,
        Position = session.Position,
        InventoryChanges = session.GetInventoryDelta(),
        // ... 현재 상태만 포함
    };
}
```

---

## 🔒 멱등성 (Idempotency) 보장

### 문제 상황

네트워크 끊김 후 클라이언트가 마지막 요청을 재전송했을 때:
- **서버는 이미 처리했는데 응답(Ack)만 클라이언트가 못 받은 경우**
- 로직을 두 번 실행하면 안 됨 (아이템 중복 소모 등)

### 해결 방법: Sequence ID 기반 중복 검사

**UserSession 구조**:
```csharp
public class UserSession
{
    public long LastProcessedSequenceId { get; private set; }

    // 최근 처리된 요청의 결과 캐시 (짧은 TTL)
    private readonly LRUCache<long, byte[]> _responseCache = new(capacity: 100);

    public async Task<byte[]?> ProcessPacketAsync(long sequenceId, byte[] payload)
    {
        // 1. 이미 처리된 요청인가?
        if (sequenceId <= LastProcessedSequenceId)
        {
            _logger.LogWarning(
                "Duplicate request detected: SeqId={SeqId} (already processed)",
                sequenceId);

            // 캐시된 응답 반환 (로직 재실행 X)
            if (_responseCache.TryGet(sequenceId, out var cachedResponse))
            {
                return cachedResponse;
            }

            // 캐시 없으면 현재 상태 기반 응답
            return CreateIdempotentResponse(sequenceId);
        }

        // 2. Sequence ID 점프 감지 (누락 확인)
        if (sequenceId > LastProcessedSequenceId + 1)
        {
            _logger.LogWarning(
                "Sequence gap detected: Expected {Expected}, Got {Got}",
                LastProcessedSequenceId + 1, sequenceId);

            // 클라이언트에게 누락 알림 → 재접속 유도
            return CreateSyncRequiredResponse();
        }

        // 3. 정상 처리
        var response = await ExecuteBusinessLogicAsync(payload);

        // 4. Sequence ID 업데이트 및 캐싱
        LastProcessedSequenceId = sequenceId;
        _responseCache.Add(sequenceId, response);

        return response;
    }
}
```

**패킷 구조** (Sequence ID 포함):
```csharp
public struct ClientPacketHeader
{
    public int OpCode;
    public long SequenceId;      // 클라이언트가 관리하는 송신 순서
    public long AckSequenceId;   // 클라이언트가 마지막으로 받은 서버 패킷 순서
    public int PayloadLength;
}
```

---

## 🏗️ Gateway 세션별 라우팅 관리

### ❌ 잘못된 방식: 싱글톤 Dictionary

```csharp
// 안티패턴
public class SessionMapper
{
    private static ConcurrentDictionary<Guid, long> _socketToGameServer = new();

    // 문제점:
    // 1. Lock 경합 (수만 개 소켓 동시 접근)
    // 2. 메모리 릭 위험 (Remove 누락 시)
    // 3. GC 압박 (Dictionary 리사이징)
}
```

### ✅ 올바른 방식: 세션 객체 내부 관리

```csharp
public class GatewaySession : TcpSession
{
    private readonly GamePacketRouter _packetRouter;

    // 세션 내부에 라우팅 정보 저장 (Lock-Free)
    public long PinnedGameServerNodeId { get; private set; }
    public long SessionId { get; private set; }

    public GatewaySession(TcpServer server, GamePacketRouter packetRouter) : base(server)
    {
        _packetRouter = packetRouter;
        PinnedGameServerNodeId = 0; // 0 = 미고정
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        var packet = buffer.AsSpan((int)offset, (int)size).ToArray();

        // O(1) 메모리 참조 (Dictionary 조회 없음)
        if (PinnedGameServerNodeId == 0)
        {
            // 미고정: Round-robin
            _packetRouter.ForwardToGameServer(Id, packet);
        }
        else
        {
            // 고정: Direct 전송 (Lock-Free)
            _packetRouter.ForwardToSpecificGameServer(
                PinnedGameServerNodeId,
                Id,
                SessionId,
                packet);
        }
    }

    // GameServer로부터 PinSession 명령 수신 시 호출
    public void Pin(long gameServerNodeId, long sessionId)
    {
        PinnedGameServerNodeId = gameServerNodeId;
        SessionId = sessionId;
    }

    // Re-pinning (재접속 시 다른 서버로 변경)
    public void Reroute(long newTargetNodeId)
    {
        PinnedGameServerNodeId = newTargetNodeId;
    }

    protected override void OnDisconnected()
    {
        // 소켓 정리 시 라우팅 정보도 자동 해제
        // (객체 소멸로 자연스럽게 GC됨)
        PinnedGameServerNodeId = 0;
        SessionId = 0;
    }
}
```

**장점**:
1. **Lock-Free**: Dictionary 동기화 오버헤드 0
2. **O(1) 접근**: 필드 직접 참조
3. **자동 정리**: 소켓 종료 시 객체 소멸로 자연스럽게 해제
4. **캐시 친화적**: Session 객체와 라우팅 정보가 메모리상 인접

---

## 📊 P2P 프로토콜 확장

### 새로운 제어 명령 추가

```csharp
public enum ControlCommand : byte
{
    PinSession = 1,         // 세션 고정
    DisconnectClient = 2,   // 클라이언트 연결 해제
    RerouteSocket = 3       // 소켓 라우팅 변경 (재접속용)
}

public struct ReroutePayload
{
    public long TargetNodeId;  // 새로운 목적지 GameServer

    public byte[] Serialize() { /* ... */ }
    public static ReroutePayload Deserialize(ReadOnlySpan<byte> data) { /* ... */ }
}
```

---

## 🎯 구현 체크리스트

### Gateway
- [ ] GatewaySession에 `PinnedGameServerNodeId` 필드 추가
- [ ] `Pin()`, `Reroute()` 메서드 구현
- [ ] GamePacketRouter에서 `RerouteSocket` 제어 명령 처리
- [ ] SessionMapper 제거 (세션별 관리로 전환)

### GameServer
- [ ] Redis 세션 조회 로직 (`GetAsync`, `SetAsync`, `DeleteAsync`)
- [ ] 중복 로그인 처리 (Service Mesh Kick-out 요청/응답)
- [ ] 재접속 처리 (Re-pinning 지시)
- [ ] Sequence ID 기반 멱등성 검사
- [ ] 스냅샷 동기화 패킷 생성

### Service Mesh
- [ ] `KickoutRequest` / `KickoutAck` 메시지 정의
- [ ] NodeCommunicator에 Kick-out 핸들러 등록

### Protocol
- [ ] `StateSnapshotPacket` 정의
- [ ] `ReconnectRequest` / `ReconnectResponse` 정의
- [ ] 클라이언트 패킷 헤더에 `SequenceId`, `AckSequenceId` 추가

---

## 🔮 향후 고도화 방안

1. **패킷 캐싱** (선택적):
   - 서버가 보낸 패킷을 Ring Buffer에 캐싱
   - 재접속 시 누락 범위만 재전송
   - 메모리 vs UX 트레이드오프

2. **Heartbeat**:
   - Gateway ↔ Client 간 주기적 Ping/Pong
   - 연결 끊김 조기 감지

3. **Session Timeout**:
   - Redis에 TTL 설정 (예: 5분)
   - 재접속하지 않으면 자동 정리

4. **분산 트레이싱**:
   - 패킷에 Trace ID 추가
   - Gateway → GameServer → Service 전체 추적

---

**문서 버전**: 2.0
**최종 수정일**: 2026-02-25
**작성자**: NetworkEngine Team
