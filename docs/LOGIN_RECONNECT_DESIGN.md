# 로그인 / 중복 로그인 / 재접속 설계 명세서

## 📌 개요

라이브러리(SimpleNetEngine.Game)와 앱(GameSample) 사이의 명확한 경계 분리를 기반으로,
로그인, 중복 로그인, Heartbeat, Disconnect/Reconnect 전체 플로우를 정의한다.

**핵심 원칙**: 라이브러리가 인증 플로우의 **골격(Skeleton)**을 소유하고, 앱이 **Hook으로 비즈니스 로직을 주입**한다.

---

## 🏗️ 라이브러리 vs 앱 경계

### 라이브러리 (SimpleNetEngine.Game) — 인프라 제공

| 컴포넌트 | 책임 |
|---------|------|
| `HandshakeController` | ECDH 키 교환, Created → Authenticating 전이 |
| `LoginController` | Redis 세션 조회/등록, 중복 감지, 3-시나리오 분기, Active 전이 |
| `ReconnectController` | ReconnectKey 검증, Actor 복원, Re-pin |
| `KickoutController` | Kickout RPC 수신, Actor 제거 |
| `ConnectionController` | NewUserNtf Actor 생성, Disconnect 처리 |
| `KickoutMessageHandler` | Kickout RPC 발신 (Cross-Node) |
| `ISessionStore` | Redis SSOT 세션 저장소 |
| `ISessionActorManager` | Actor 생명주기 관리 |
| `ILoginHandler` | **앱이 구현할 인터페이스** (확장점) |

### 앱 (GameSample) — 비즈니스 로직 구현

| 컴포넌트 | 책임 |
|---------|------|
| `GameLoginHandler : ILoginHandler` | JWT 검증, DB 유저 조회, 게임 데이터 로드/저장 |
| 각종 GameController | 게임별 패킷 핸들러 |

---

## 🔌 ILoginHandler 인터페이스 (앱의 확장점)

```csharp
public interface ILoginHandler
{
    // 1. ExternalId → UserId 변환 (JWT, OAuth 등 게임별 인증)
    Task<LoginAuthResult> AuthenticateAsync(string externalId, ISessionActor actor);

    // 2. 로그인 성공 후 Actor 초기화 (DB 로드, 게임 상태 설정)
    //    이 Hook 실행 후 라이브러리가 actor.Status = Active 전이
    Task OnLoginSuccessAsync(ISessionActor actor);

    // 3. 재접속 성공 시 (Disconnected → Active 복원)
    //    gapInfo를 확인하여 유실 패킷 대응 (상태 스냅샷 재전송, 클라이언트 재전송 요청 등)
    Task OnReconnectedAsync(ISessionActor actor, ReconnectGapInfo gapInfo);

    // 4. Kickout 수신 시 (중복 로그인에 의한 강제 퇴장)
    //    게임 데이터 저장 후 DisconnectAction 반환
    Task<DisconnectAction> OnKickoutAsync(ISessionActor actor, KickoutReason reason);

    // 5. 비의도적 연결 끊김 → 재접속 대기 진입 시
    //    매칭 일시정지, 파티원 알림 등
    Task OnDisconnectedAsync(ISessionActor actor);

    // 6. 의도적 로그아웃 또는 Grace Period 만료
    //    최종 데이터 저장
    Task OnLogoutAsync(ISessionActor actor);
}

public readonly record struct LoginAuthResult
{
    public bool IsSuccess { get; init; }
    public long UserId { get; init; }
    public int ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static LoginAuthResult Success(long userId) =>
        new() { IsSuccess = true, UserId = userId };
    public static LoginAuthResult Failure(int errorCode, string? message = null) =>
        new() { IsSuccess = false, ErrorCode = errorCode, ErrorMessage = message };
}

public enum KickoutReason
{
    DuplicateLogin,
    AdminKick,
    Timeout
}

/// 재접속 시 SequenceId gap 정보
/// 앱 개발자가 유실 패킷을 감지하고 대응할 수 있도록 제공
public readonly record struct ReconnectGapInfo(
    ushort ClientReportedLastServerSeqId,   // 클라이언트가 마지막으로 수신한 서버 SeqId
    ushort ServerCurrentSeqId,              // 서버의 현재 SeqId (발급한 마지막 값)
    ushort ClientReportedLastClientSeqId,   // 클라이언트가 마지막으로 송신한 자신의 SeqId
    ushort ServerLastValidatedClientSeqId)  // 서버가 마지막으로 검증한 클라이언트 SeqId
{
    // 서버→클라이언트 유실 존재 → 상태 스냅샷 재전송 고려
    public bool HasServerToClientGap => ServerCurrentSeqId != ClientReportedLastServerSeqId;
    // 클라이언트→서버 유실 존재 → 클라이언트 재전송 또는 무시
    public bool HasClientToServerGap => ClientReportedLastClientSeqId != ServerLastValidatedClientSeqId;
}
```

---

## 🔄 Actor 상태 전이

```
Created → Authenticating → Active
                             ↓ (TCP drop / Pong timeout)
                          Disconnected ──[grace period 만료]──→ Disposed
                             ↓ (재접속 성공)
                           Active (복원)
                             ↓ (LogoutReq)
                          Disposed (즉시)
```

### 상태 전이 책임 분리

| 전이 | 트리거 | 책임 | 앱 Hook |
|------|--------|------|---------|
| → Created | NewUserNtf (TCP 연결) | 라이브러리 | - |
| Created → Authenticating | HandshakeReq 완료 | 라이브러리 | - |
| Authenticating → Active | LoginReq 성공 | 라이브러리 | `AuthenticateAsync` → `OnLoginSuccessAsync` |
| Active → Disconnected | TCP drop / Pong timeout | 라이브러리 | `OnDisconnectedAsync` |
| Disconnected → Active | ReconnectReq 성공 | 라이브러리 | `OnReconnectedAsync(actor, gapInfo)` |
| * → Disposed (Kickout) | 중복 로그인 | 라이브러리 | `OnKickoutAsync` |
| * → Disposed (Logout) | LogoutReq / Grace 만료 | 라이브러리 | `OnLogoutAsync` |

---

## 🔐 클라이언트 접속 플로우

### 1. 신규 유저 플로우

```
[1] Client → Gateway: TCP Connect
[2] Gateway → GameServer (Service Mesh): NewUserNtfReq
    → GameServer: 익명 Actor 생성 (Created)
[3] GameServer → Client (GSC): ReadyToHandshakeNtf
[4] Client → GameServer: HandshakeReq { clientEcdhPublicKey }
    → Actor.Status = Authenticating
[5] GameServer → Gateway (Service Mesh): ActivateEncryptionReq
    → Gateway: AES-GCM 활성화
[6] GameServer → Client: HandshakeRes { serverEcdhPublicKey, signature }
    → Client: AES-GCM 활성화
[7] Client → GameServer (ENCRYPTED): LoginGameReq { credential }
    → ILoginHandler.AuthenticateAsync(credential)
    → ILoginHandler.OnLoginSuccessAsync(actor)
    → Actor.Status = Active
    → Redis: session:user:{userId} + session:reconnect:{key} 저장
[8] GameServer → Client (ENCRYPTED): LoginGameRes { success, reconnectKey }
```

### 2. 재접속 유저 플로우

```
[1~6] 동일 (ECDH 핸드셰이크 — 새 TCP = 새 암호화 키)

[7] Client → GameServer (ENCRYPTED): ReconnectReq { reconnectKey, lastServerSequenceId, lastClientSequenceId }
    → Redis: session:reconnect:{key} → userId → SessionInfo
    → Same-Node: 로컬 Actor 복원 (Disconnected → Active)
      - gap 정보 수집 (UpdateRouting 전에 서버측 SequenceId 상태 저장)
      - actor.UpdateRouting(gatewayNodeId, lastClientSequenceId) — SequenceId 연속성 유지
      - actor.RegenerateReconnectKey() (1회용 재발급)
      - ILoginHandler.OnReconnectedAsync(actor, gapInfo) — 앱이 유실 패킷 대응
    → Cross-Node: Actor 사전 생성 + Gateway Re-route → 클라이언트 재시도
      - B → A: NtfNewUser(is_reroute=true) — A에 임시 Actor 사전 생성 (Authenticating, Handshake 스킵)
      - B → Gateway: RerouteSocketReq (pin을 A로 변경)
      - B → Client: ReconnectRes { success=false, requiresRetry=true }
      - Client가 같은 ReconnectKey로 ReconnectReq 재전송
      - Gateway가 A로 전달 → A에서 Same-Node 처리
[8] GameServer → Client: ReconnectRes { success, newReconnectKey, serverSequenceId, clientNextSequenceId }
```

**핵심 차이**: 재접속은 `LoginGameReq` 대신 `ReconnectReq` 하나로 완료 (SequenceId 동기화 포함).

### 3. 클라이언트 판단 기준

```csharp
// Unity Client
if (savedReconnectKey != null && wasUnexpectedDisconnect)
{
    var res = Send(new ReconnectReq
    {
        ReconnectKey = savedReconnectKey,
        LastServerSequenceId = lastReceivedServerSeqId,
        LastClientSequenceId = lastSentClientSeqId
    });
    if (res.Success)
        SyncSequenceId((ushort)(res.ClientNextSequenceId - 1));
}
else
    Send(new LoginGameReq { Credential = ... });
```

---

## 🔄 중복 로그인 처리

### Same-Node 중복 로그인

```
LoginGameReq 수신
  → Redis 조회: 같은 노드에 기존 세션
  → ILoginHandler.OnKickoutAsync(oldActor, DuplicateLogin)
  → KickoutNtf { Reason=DuplicateLogin } → 기존 클라이언트에 GSC 전송 (Actor 제거 전)
  → Old Actor 제거 (RemoveActor)
  → Gateway에 DisconnectClient RPC
  → 새 세션 생성 (HandleNewLogin)
```

### Cross-Node 중복 로그인

```
LoginGameReq 수신
  → Redis 조회: 다른 노드에 기존 세션
  → KickoutMessageHandler.SendKickoutRequestAsync (AWAITED, 5초 타임아웃)
    → Old Node의 KickoutController:
      - KickoutNtf { Reason=DuplicateLogin } → 기존 클라이언트에 GSC 전송 (Actor 제거/Disconnect 전)
      - ILoginHandler.OnKickoutAsync(oldActor, DuplicateLogin) → DisconnectAction 결정
      - TerminateSession: Actor 즉시 제거 + 조건부 Redis 삭제 (DeleteSessionIfMatchAsync)
      - AllowSessionResume: Disconnected 전이 + Grace Period
      - GatewayDisconnectQueue에 소켓 해제 예약
      - KickoutRes 반환
  → 새 세션 생성 (HandleNewLogin)
```

### KickoutNtf (클라이언트 알림)

```protobuf
// handshake.proto
message KickoutNtf {
  EKickoutReason Reason = 1;
}

enum EKickoutReason {
  DUPLICATE_LOGIN = 0;
  ADMIN_KICK = 1;
  TIMEOUT = 2;
}
```

**전송 시점**: Actor 제거 및 TCP 종료 **이전**에 GSC 경유로 전송.
클라이언트가 SESSION_EXPIRED 대신 정확한 kick 사유를 수신할 수 있다.
`requestId: 0` (서버 푸시), `sequenceId: actor.NextSequenceId()` 사용.

### Kickout에서 Redis 삭제 안 하는 이유

```
T0: Client-A가 GameServer-A에 로그인 (Redis: SessionA)
T1: Client-A가 GameServer-B로 재접속
T2: GameServer-B가 Redis에 SessionB 덮어씀 ✅
T3: GameServer-B → GameServer-A에 Kickout RPC
T4: GameServer-A가 Redis 삭제하면 → SessionB가 삭제됨 ❌

원칙: New node가 Redis를 덮어쓰면 키 소유권 획득.
      Old node는 로컬 Actor만 정리.
```

---

## 💓 Heartbeat (Ping/Pong)

> 상세 설계: [HEARTBEAT_DESIGN.md](HEARTBEAT_DESIGN.md)

### Client-driven + GameServer Inactivity 감지

```
Client ──PingReq──→ Gateway ──→ GameServer (Actor mailbox)
Client ←──PongRes──── Gateway ←── GameServer

- Client: idle 시에만 PingReq 전송 (PingInterval: 5초)
- GameServer: 모든 패킷 수신 시 lastActivityTicks 갱신
- GameServer: InactivityTimeout (30초) 초과 시 Disconnect 지시
```

### 설정

```csharp
public class GameOptions
{
    public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReconnectGracePeriod { get; set; } = TimeSpan.FromSeconds(30);
}
```

### 타임라인 (비의도적 연결 끊김)

```
0s    — 네트워크 단절 (클라이언트 PingReq 도달 불가)
30s   — InactivityScanner: Inactivity 감지
        → ServiceMeshDisconnectClientReq (Gateway에 TCP 종료 지시)
        → ClientDisconnectedNtf → actor.Status = Disconnected
        → ILoginHandler.OnDisconnectedAsync(actor)
        → Grace Period 타이머 시작
60s   — Grace Period 만료 (30초)
        → 재접속 안 함 → ILoginHandler.OnLogoutAsync(actor)
        → Actor Disposed, Redis 삭제
```

---

## 🔑 ReconnectKey 관리

### Redis 저장 구조

```
session:user:{userId}         → SessionInfo (JSON)
session:reconnect:{reconnectKey} → userId (역인덱스)
```

### SessionInfo 확장

```csharp
public record SessionInfo(
    long UserId,
    long SessionId,
    long GameServerNodeId,
    long GatewayNodeId,
    Guid SocketId,
    DateTimeOffset LoginTime,
    Guid ReconnectKey           // 추가
);
```

### ISessionStore 확장

```csharp
public interface ISessionStore
{
    Task<SessionInfo?> GetSessionAsync(long userId);
    Task SetSessionAsync(long userId, SessionInfo session);
    Task DeleteSessionAsync(long userId);
    Task<SessionInfo?> GetSessionByReconnectKeyAsync(Guid reconnectKey);  // 추가
}
```

### ReconnectKey 생명주기

| 이벤트 | 동작 |
|--------|------|
| 최초 로그인 (LoginGameRes) | 발급 (1세대) |
| 재접속 성공 (ReconnectRes) | old 폐기 → new 재발급 (N+1세대) |
| Grace Period 만료 | 폐기 (Actor Disposed) |
| Kickout | 폐기 |
| Logout | 폐기 |

### Redis 업데이트 시점

| 이벤트 | Redis 작업 |
|--------|-----------|
| 최초 로그인 | `SET session:user:{userId}` + `SET session:reconnect:{key}` |
| 재접속 성공 | `DEL session:reconnect:{oldKey}` + `SET session:reconnect:{newKey}` + `UPDATE session:user:{userId}` |
| Kickout / Logout / 만료 | `DEL session:user:{userId}` + `DEL session:reconnect:{key}` |

---

## 🔀 재접속 시 Actor 2개 문제

재접속 시 일시적으로 Actor가 2개 존재:
- **기존 Actor** (Disconnected 상태, 게임 데이터 보유)
- **새 Actor** (Created → Authenticating, ECDH용 임시)

```
ReconnectReq 성공 시:
  1. 새 Actor (임시) 제거
  2. 기존 Actor의 SocketId, GatewayNodeId를 새 값으로 교체
  3. Gateway Re-pin (새 TCP → 기존 Actor)
  4. 기존 Actor.Status = Active
  5. ReconnectKey 재발급
```

---

## 🔒 분산 락 (동시 로그인 방지)

```csharp
// LoginController 내부
var lockKey = $"login:{userId}";
await using var lockObj = await DistributeLockObject.CreateAsync(
    _redis, lockKey, expiryTime: TimeSpan.FromSeconds(10));

if (lockObj == null)
    return Response.Error(ErrorCode.TooManyRequests, "다른 로그인 처리 중");

// 락 안에서 안전하게 Redis 조회 → 분기
```

---

## 📝 Proto 정의 (추가)

```protobuf
// 재접속
message ReconnectReq {
    string reconnect_key = 1;
    uint32 last_server_sequence_id = 2;   // 클라이언트가 마지막 수신한 서버 SeqId
    uint32 last_client_sequence_id = 3;   // 클라이언트가 마지막 송신한 자신의 SeqId
}

message ReconnectRes {
    bool   success = 1;
    string new_reconnect_key = 3;
    string error_message = 4;
    bool   requires_retry = 5;
    uint32 server_sequence_id = 6;        // 서버의 현재 SeqId (gap 감지용)
    uint32 client_next_sequence_id = 7;   // 클라이언트가 사용할 다음 SeqId
}

// Heartbeat
message PingReq { int64 timestamp = 1; }
message PongRes { int64 timestamp = 1; }

// 로그아웃
message LogoutReq { }
message LogoutRes { bool success = 1; }
```

---

## 📊 구현 그룹

| 그룹 | 작업 내용 | 의존성 |
|------|----------|--------|
| G1 | 설계 문서 작성 | - |
| G2 | ILoginHandler + 타입 + GameLoginHandler 샘플 | G1 |
| G3 | KickoutController 이동 + Disconnect Hook + Redis 삭제 버그 | G2 |
| G4 | LoginReq 진입점 + Task.Delay 제거 + 분산 락 | G3 |
| G5 | Ping/Pong + Grace Period + LogoutReq | G3 |
| G6 | Redis ReconnectKey + ReconnectReq/Res + Same/Cross-Node 재접속 | G4, G5 |
| G7 | 문서 최종 업데이트 | G6 |

---

## ✅ 구현 완료 상태

| 그룹 | 상태 | 주요 파일 |
|------|------|-----------|
| G2 | ✅ 완료 | `ILoginHandler.cs`, `GameLoginHandler.cs` |
| G3 | ✅ 완료 | `KickoutController.cs`, `ConnectionController.cs` |
| G4 | ✅ 완료 | `LoginController.cs` (DistributeLockObject) |
| G5 | ✅ 완료 | `GatewaySession.cs` (Ping/Pong), `SessionActor.cs` (GracePeriod), `LoginController.cs` (Logout) |
| G6 | ✅ 완료 | `ReconnectController.cs`, `RedisSessionStore.cs` (ReconnectKey 역인덱스) |
| G7 | ✅ 완료 | 문서 최종 업데이트 |

---

**문서 버전**: 2.1
**작성일**: 2026-03-13
**기반 문서**: `docs/SESSION_LIFECYCLE.md`, `docs/ENCRYPTION_COMPRESSION_DESIGN.md`
