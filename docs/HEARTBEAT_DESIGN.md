# Heartbeat 설계 명세서

## 개요

Client-driven Heartbeat + GameServer Inactivity 감지 방식으로 설계한다.
기존 Gateway 주도 Ping/Pong 방식은 Dumb Proxy 원칙을 위반하므로 폐기한다.

클라이언트가 idle 시에만 PingReq를 보내고, GameServer가 Inactivity를 감지하여 Disconnect를 결정한다.

---

## 기존 설계 vs 변경 설계

| | 기존 (폐기) | 변경 (신규) |
|---|---|---|
| **Ping 주체** | Gateway → Client | Client → Gateway → GameServer |
| **Ping 조건** | 고정 타이머 (10초 간격) | Idle 시에만 (일반 트래픽 있으면 불필요) |
| **감시 위치** | Gateway (PongRes 대기) | GameServer (Actor.lastActivityTicks) |
| **타임아웃 액션** | Gateway가 TCP 강제 종료 | GameServer가 Disconnect 지시 → Grace Period |
| **아키텍처 준수** | Dumb Proxy 위반 4건 | 표준 Stateful 요청 플로우 |

### 기존 방식의 Dumb Proxy 위반 사항

1. **Opcode 파싱**: `IsPongResponse()`에서 MsgId 읽기
2. **패킷 인터셉트**: PongRes를 GameServer에 전달하지 않고 Gateway에서 소비
3. **패킷 생성**: `SendPingReq()`에서 PingReq 패킷을 직접 생성
4. **Disconnect 결정**: Pong 타임아웃 시 Gateway가 독자적으로 연결 종료 판단

---

## Heartbeat 아키텍처

### 데이터 플로우

```
Client → Gateway → GameServer (PingReq)  [표준 Stateful 요청 플로우]
  ↓
GameServer: Actor.lastActivityTicks 갱신 + PongRes 생성
  ↓
GameServer → Gateway → Client (PongRes)  [표준 응답 플로우]
```

### 역할 분리

| 컴포넌트 | 책임 |
|---------|------|
| **Client (NetClient)** | idle 감지, PingReq 전송 |
| **Gateway** | 패킷 통과만 (Dumb Proxy) |
| **GameServer (Actor)** | lastActivityTicks 갱신, PongRes 응답 |
| **GameServer (InactivityScanner)** | 주기적 스캔, Inactivity 감지 → Disconnect |

---

## 클라이언트 측 설계

### Idle-based Ping

- 마지막 패킷 **전송** 시간 추적 (`_lastSentTicks`)
- 모든 `SendAsync`/`RequestAsync` 호출 시 타이머 리셋
- idle 시간 > `PingInterval` → PingReq 자동 전송
- 일반 req/res 트래픽이 있으면 Ping 불필요 (트래픽 절약)

```csharp
// NetClient 내부
private long _lastSentTicks;
private Timer? _idlePingTimer;

// 패킷 전송 시마다 갱신
private void OnPacketSent()
{
    Volatile.Write(ref _lastSentTicks, Stopwatch.GetTimestamp());
}

// Timer 콜백: idle 체크 → PingReq 전송
private void OnIdlePingTick(object? state)
{
    var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastSentTicks));
    if (elapsed > _pingInterval)
    {
        SendPingReq();  // 일반 패킷처럼 전송
    }
}
```

### 설정

```csharp
public class NetClientOptions
{
    /// <summary>
    /// Idle 상태에서 PingReq 전송 간격.
    /// 이 시간 동안 패킷을 보내지 않으면 PingReq를 자동 전송.
    /// </summary>
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(5);
}
```

---

## GameServer 측 설계

### 1. Actor Activity 추적

모든 패킷 수신 시 `lastActivityTicks`를 갱신한다.
PingReq뿐 아니라 **모든 클라이언트 패킷**이 Activity로 인정된다.

```csharp
public interface ISessionActor
{
    // 기존 프로퍼티들...
    long LastActivityTicks { get; }
    void TouchActivity();
}
```

- `TouchActivity()`: `Volatile.Write(ref _lastActivityTicks, Stopwatch.GetTimestamp())`
- 호출 위치: Actor mailbox에서 메시지 처리 시 (모든 패킷 공통)

### 2. PingReq 핸들러

```csharp
[NodeController]
public class HeartbeatController
{
    [UserPacketHandler]
    public Response HandlePing(PingReq req)
    {
        // lastActivityTicks는 mailbox 진입 시 이미 갱신됨
        return Response.Ok(new PongRes { Timestamp = req.Timestamp });
    }
}
```

- PingReq는 일반 패킷처럼 Actor mailbox를 통과
- mailbox 진입 시 `TouchActivity()` 자동 호출 → 별도 갱신 불필요
- PongRes를 표준 응답으로 반환

### 3. InactivityScanner

```csharp
public class InactivityScanner : BackgroundService
{
    private readonly ISessionActorManager _actorManager;
    private readonly TimeSpan _inactivityTimeout;
    private readonly TimeSpan _scanInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_scanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            ScanInactiveActors();
        }
    }

    private void ScanInactiveActors()
    {
        var now = Stopwatch.GetTimestamp();
        foreach (var actor in _actorManager.GetAllActors())
        {
            if (actor.Status != ActorState.Active) continue;

            var elapsed = Stopwatch.GetElapsedTime(actor.LastActivityTicks, now);
            if (elapsed > _inactivityTimeout)
            {
                // Gateway에 TCP 종료 지시 → ClientDisconnectedNtf → Grace Period
                DisconnectClient(actor);
            }
        }
    }
}
```

### 설정

```csharp
public class GameOptions
{
    /// <summary>
    /// 클라이언트 Inactivity 타임아웃.
    /// 이 시간 동안 아무 패킷도 수신하지 못하면 Disconnect 처리.
    /// </summary>
    public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
```

---

## 타임라인 (비의도적 연결 끊김)

### 정상 Idle 유지

```
0s    — 클라이언트 idle 시작
5s    — Client: PingReq 전송 → GameServer: lastActivityTicks 갱신 + PongRes
10s   — Client: PingReq 전송 → GameServer: lastActivityTicks 갱신 + PongRes
...   — (계속 반복, 연결 유지)
```

### 네트워크 단절 시

```
0s    — 네트워크 단절 (클라이언트 PingReq 도달 불가)
30s   — InactivityScanner: Inactivity 감지
        → GameServer: ServiceMeshDisconnectClientReq (Gateway에 TCP 종료 지시)
        → Gateway: TCP 종료 → ClientDisconnectedNtf 발신
        → GameServer: actor.Status = Disconnected
        → ILoginHandler.OnDisconnectedAsync(actor)
        → Grace Period 타이머 시작
60s   — Grace Period 만료 (30초)
        → 재접속 안 함 → ILoginHandler.OnLogoutAsync(actor)
        → Actor Disposed, Redis 삭제
```

### 일반 트래픽이 있는 경우

```
0s    — 클라이언트가 게임 패킷 전송 중
5s    — GameServer: lastActivityTicks 갱신 (게임 패킷)
7s    — GameServer: lastActivityTicks 갱신 (게임 패킷)
...   — PingReq 불필요 (일반 트래픽이 Activity 유지)
```

---

## 제거 대상 (Gateway)

### GatewaySession.cs
- `StartHeartbeat()`, `StopHeartbeat()`
- `OnHeartbeatTick()`, `SendPingReq()`
- `OnPongReceived()`, `IsPongResponse()`
- 필드: `_heartbeatEnabled`, `_heartbeatTimer`, `_pingInterval`, `_pongTimeout`, `_lastPingSentTicks`, `_waitingForPong`
- `OnConnected()`에서 `StartHeartbeat()` 호출 제거
- `ProcessSinglePacket()`에서 `IsPongResponse()` 인터셉트 로직 제거

### GatewayTcpServer.cs
- 필드: `_heartbeatEnabled`, `_pingInterval`, `_pongTimeout`
- 생성자에서 해당 값 읽기 제거
- `CreateSession()` 파라미터에서 제거

### GatewayOptions.cs
- `EnableHeartbeat`, `PingInterval`, `PongTimeout` 프로퍼티 제거

---

## 추가 대상 (GameServer)

### ISessionActor / SessionActor
- `long LastActivityTicks` 프로퍼티
- `void TouchActivity()` 메서드

### GameOptions
- `TimeSpan InactivityTimeout` (기본 30초)

### HeartbeatController
- `HandlePing(PingReq)` → `PongRes` 응답

### InactivityScanner : BackgroundService
- PeriodicTimer 기반 주기적 스캔
- 스캔 간격: `InactivityTimeout / 3` (약 10초)
- Inactive Actor 감지 시 `ServiceMeshDisconnectClientReq` RPC 전송

---

## 설계 결정 사항

### Q: 왜 Gateway가 아닌 GameServer에서 Heartbeat를 처리하는가?

**A**: Gateway는 Dumb Proxy로, 패킷 내용을 파싱하거나 disconnect를 결정하는 것은 역할 범위를 벗어남.
GameServer가 세션 상태(Actor)를 소유하므로, idle 감지 → disconnect → grace period 전환까지
일관된 흐름으로 처리할 수 있다.

### Q: 왜 Client-driven Ping인가?

**A**: 서버가 수천 클라이언트에게 주기적으로 Ping을 보내는 것보다,
클라이언트가 idle 시에만 자발적으로 Ping을 보내는 것이 서버 부하와 트래픽 측면에서 효율적.
Photon(UDP), gRPC 등 업계 표준 패턴과도 일치.

### Q: 왜 Idle-based인가?

**A**: 일반 req/res 트래픽이 오고 있으면 별도의 Ping은 불필요한 오버헤드.
모든 패킷이 Activity로 인정되므로, 트래픽이 있는 동안은 자동으로 연결이 유지된다.

### Q: InactivityTimeout과 Grace Period의 관계는?

**A**: 2단계 타임아웃 구조:
1. **InactivityTimeout (30초)**: 패킷 미수신 → Disconnect 판정 (재접속 가능)
2. **GracePeriod (30초)**: Disconnect 후 재접속 대기 (재접속 불가 시 Cleanup)
총 60초 내에 재접속하면 세션 복원 가능.
