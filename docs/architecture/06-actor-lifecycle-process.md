# Actor Lifecycle Process: Gateway 주도 Actor 생성 및 보호 방안

## 1. 개요 및 컴포넌트 구조 (Overview & Component Map)

### 1.1 컴포넌트 맵

| 컴포넌트 | 파일 | 역할 |
|-----------|------|------|
| GatewaySession | `SimpleNetEngine.Gateway/Network/GatewaySession.cs` | TCP 커넥션 관리, 라우팅 고정(Pinning), 패킷 포워딩 |
| GamePacketRouter | `SimpleNetEngine.Gateway/Network/GamePacketRouter.cs` | P2P 라우터 소켓, 노드 간 메시지 송신 |
| SessionMapper | `SimpleNetEngine.Gateway/Core/SessionMapper.cs` | SocketId -> (NodeId, SessionId) 매핑 관리 |
| ControlController | `SimpleNetEngine.Gateway/Controllers/ControlController.cs` | Service Mesh RPC: Pin/Disconnect/Reroute 처리 |
| GatewayNodeEventController | `SimpleNetEngine.Gateway/Core/GatewayNodeEventController.cs` | 노드 참여/이탈 감지 -> P2P 연결 갱신 |
| GatewayPacketListener | `SimpleNetEngine.Game/Network/GatewayPacketListener.cs` | P2P 패킷 수신 대기, 핸들러로 디스패치 |
| GameServerHub | `SimpleNetEngine.Game/Core/GameServerHub.cs` | 세션 생명주기 제어 및 Actor 생성 (Smart Hub) |
| PacketHandlerMiddleware | `SimpleNetEngine.Game/Middleware/PacketHandlerMiddleware.cs` | 패킷 가로채기 및 Actor 상태 기반(State-based) 인가 파이프라인 |
| ActorManager | `SimpleNetEngine.Game/Actor/ActorManager.cs` | SessionId -> IActor 매핑 캐시 및 수명 관리 |
| SessionActor | `SimpleNetEngine.Game/Actor/SessionActor.cs` | Channel<T> 기반 메일박스 액터, 스코프 DI 적용 |
| RedisSessionStore | `SimpleNetEngine.Game/Session/RedisSessionStore.cs` | Redis SSOT: UserId -> SessionInfo |
| KickoutMessageHandler | `SimpleNetEngine.Game/Network/KickoutMessageHandler.cs` | 타 노드로 Kickout RPC 요청 발송 |
| SessionController | `Sample/GameSample/Controllers/SessionController.cs` | Kickout RPC 수신 처리 (Inbound) |

---

## 2. 상태 전이 다이어그램 (State Transition Diagrams)

### 2.1 Actor 상태 (ActorState FSM)
*Actor는 메모리 상에서 명확한 상태(ActorState)를 가지며, 이 상태를 기반으로 PacketHandler가 권한을 검증합니다.*

```
           +------------------+
           |      NONE        | 메모리 미할당 상태
           +--------+---------+
                    |
          HandshakeReq 수신 (TCP Handshake 완료)
                    |
                    v
           +------------------+
           |     Created      | 익명 Actor 할당. 인증 전. (SequenceId, Reconnect Key 발급)
           +--------+---------+
                    |
                Login_Req 수신
                    |
                    v
           +------------------+
           |  Authenticating  | 비즈니스 로그인 시도 중 (Redis 조회, 중복 로그인 킥아웃 등)
           +--------+---------+
                    |
         Redis 저장 완료 및 식별자 바인딩
                    |
                    v
           +------------------+
           |      Active      | 로그인 완료. 정상적인 인게임 패킷 처리 통과
           +--------+---------+
                   /|\
     UpdateRouting  |  네트워크 단절 (Reconnect 대기)
     (재접속 시도)   v
           +------------------+
           |   Disconnected   | 커넥션 일시 단절, Actor 메모리는 생존 유지
           +--------+---------+
                    |
          TTL 만료 또는 완전 로그아웃
                    |
                    v
           +------------------+
           |     Disposed     | 리소스 완전 파기, 메일박스 종료
           +------------------+
```

### 2.2 Redis 세션 상태 (SSOT FSM)

```
            +------------------+
            |      NONE        |  데이터 없음
            +--------+---------+
                     |
          SetSessionAsync(userId, sessionInfo)
                     |
                     v
            +------------------+
            |     ACTIVE       |  데이터 존재
            |                  |  {GameServerNodeId, SessionId, SocketId, TTL}
            +--------+---------+
                    /|\
          SetSessionAsync   DeleteSessionAsync
          (덮어쓰기)         (로그아웃/킥오프)
                    |              |
                    v              v
            +------------------+  +------------------+
            |   UPDATED        |  |      NONE        |
            | (새로운 세션)    |  | (정리됨)         |
            +------------------+  +------------------+
```

---

## 3. 새로운 생명주기 흐름 (Sequence Diagrams)

본 아키텍처는 과거의 비동기 PinSession에 의한 Race Condition(패킷 유실)을 차단하기 위해 **"Gateway 주도 Actor 생성 (Gateway-Driven Actor Creation)"** 모델을 채택합니다.

### 3.1 Scenario 1: 신규 접속 및 로그인 (New Login)

```mermaid
sequenceDiagram
    participant C as Client
    participant GW as GatewaySession
    participant GPR as GamePacketRouter
    participant Hub as GameServerHub
    participant Redis as Redis SSOT
    participant Actor as SessionActor

    C->>GW: TCP Connect
    Note over GW: 1. 커넥션 연결 및 자체 보안 협상 (대칭키 생성)<br/>2. 임의 노드로 타겟 라우팅 고정 (Pin) only<br/>   → Actor 생성 없음!
    GW->>GPR: Target GameServer 할당 및 PinnedNodeId 확정

    C->>GW: HandshakeReq ←••• 최초 패킷 (TCP Connect 후 클라이언트가 보내는 첫 메시지)
    GW->>Hub: [P2P] HandshakeReq 포워딩 (Dumb Proxy)
    Note over Hub: 3. HandshakeReq MsgId 확인 → HandleHandshakeAsync() 호출
    Hub->>Actor: Create Actor
    Note over Actor: State = Created
    Hub->>Hub: SessionId / Reconnect Key 자체 발급
    Hub->>GW: [P2P] HandshakeRes (SessionId, ReconnectKey)
    Note over GW: 💡 GW 측 대기 후, 자신이 생성한 대칭키를 덧붙임
    GW->>C: HandshakeRes (대칭키 + SessionId + ReconnectKey)로 1회 응답
    
    C->>GW: Login_Req (Token, SessionId)
    GW->>Hub: [P2P] Pinned 라우팅 진행
    Hub->>Actor: 수신 완료
    Note over Actor: State = Authenticating
    
    Actor->>Redis: GetSession / SetSession 확립 (SSOT)
    Note over Actor: 중복 로그인(Kickout) 등 비즈니스 코어 검증 처리
    
    Note over Actor: UserId 바인딩 및 State = Active 전환
    Actor->>GW: [P2P] Login_Res (SUCCESS)
    GW->>C: 클라이언트 성공 전달 (이후 모든 패킷은 Active 권한 요구)
```

---

## 4. 기존 구조의 타이밍 현안 분석 (Resolved Issues Overview)

이전 버전의 아키텍처 분석에서 발견된 주요 병목과 Race Condition은 이번 개편을 통해 모두 해소하거나 구조적으로 회피할 수 있도록 재설계되었습니다.

| 이슈 ID | 원인 및 현상 | 이번 설계에서의 해결 방안 | 상태 |
|----|-----|----------|--------|
| **T1 : Unpinned 라운드로빈 오작동** | Actor가 생성되기 전에 Gateway가 임의의 GameServer들로 패킷을 라운드로빈하며 흩뿌리는 문제 | Gateway 접속 즉시 첫 라우팅 대상 GameServer를 결정하고 Pinning. 이후 라운드로빈 제거. | 해결됨 |
| **T2 : PinSession Race Condition** | GameServer가 로그인 성공을 반환한 후에도 Gateway가 PinService RPC를 비동기로 전송 받아 타이밍이 엇갈려, 그 사이 도착한 클라이언트의 후속 패킷이 다른 노드로 유실되는 현상 (CRITICAL) | Gateway 측에서 최초부터 핀을 박고 시작하므로 네트워크 비동기 엇갈림이 원천 차단 | 해결됨 |
| **T3 : Actor 중복 생성 레이스** | 동일 계정으로 동시 다중 로그인 시 Redis 쓰기 충돌 발생 및 Actor 고아(메모리 릭) 현상 | `Login` 관련 내부 파이프라인 진입 시 특정 `UserId` 기반의 **분산 락(Distributed Lock)** 적용 확립 | 제안 적용 |
| **T4 : Cross-Node 킥아웃의 불완전성** | 타 노드로 재접속 시뮬레이션 시, 이전 노드의 Actor 소켓만 끊고 Actor 자체를 수거하지 않아 고아 Actor 발생 위험 | `HandleCrossNodeReconnect()` 흐름 내에서 명확하게 구 노드를 향한 `KickoutRequest(AWAITED)` 절차 추가 | 보완 적용 |
| **T5 : 매직 딜레이 의존(Task.Delay)** | 중복 유저 킥아웃 시, `GatewaySession` 강제 종료를 호출하고 100ms 지연 후 진행하여 퍼포먼스 불안 유발 | 임의 지연코드를 제거하고 `DisconnectClientAsync` RPC에 대한 Task Await 로 전면 비동기로 전환 | 보완 적용 |
| **T6 : 롤백 로직 부재** | 레디스 저장 실패 시 생성된 Actor가 그대로 캐싱되는 등 롤백 파이프라인 부재 | 트랜잭션 흐름을 Actor 사전 추가 -> Redis 저장으로 맞추고, 예외 발생시 try-catch로 Actor를 즉각 삭제하는 Rollback 로직 추가 | 극복 적용 |

---

## 5. Security Validation Pipeline (속성 기반의 패킷 권한 제어)

새로운 Actor LifeCycle 환경에서는 패킷 핸들러(메서드) 단위의 선언적 Attribute(어트리뷰트)를 기반으로 **요청 파이프라인 진입 즉시 미인가 패킷을 원천 차단**할 수 있습니다. 

* **기반 요소**: C# 리플렉션을 통해 읽히는 커스텀 속성 (`[RequireActorState]`, `[AllowAnonymousActor]`)
* **동작 레이어**: `IMessageDispatcher`의 `DispatchAsync` 구현부 캐시에 저장되어, 실제 핸들러 로직이 타기 전 가장 앞단에서 차단(Drop).

### 5.1 패킷 핸들러 보안 적용 예시 (Security Decorators)

```csharp
[UserController]
public class EchoController(ILogger<EchoController> logger)
{
    // 로그인 패킷 사례: Actor가 막 할당된 익명 상태(Created)인 경우만 승인. 만약 이미 Active 상태이거나 인증중이면 거부!
    [PacketHandler(EchoReq.MsgId, false)] // 인증 플래그(예: false/Authent)
    [RequireActorState(ActorState.Created)]
    public Task<Response> Echo(IActor actor, EchoReq req)
    {
        logger.LogInformation(
            "Echo request: ActorId={ActorId}, UserId={UserId}, Message={Message}",
            actor.ActorId, actor.UserId, req.Message);

        // 검증 로직 수행...
        // 상태 전이: actor.TransitionState(ActorState.Active);

        return Task.FromResult(Response.Ok(new EchoRes
        {
            Message = $"Echo: {req.Message}",
            Timestamp = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }));
    }
}

[UserController]
public class GameController(ILogger<GameController> logger)
{
    // 인게임 주요 패킷 파편화: 인증 과정을 모두 거친 Active 객체만 수용
    [PacketHandler(MoveReq.MsgId, true)] 
    [RequireActorState(ActorState.Active)]
    public Task<Response> Move(IActor actor, MoveReq req)
    {
        // 실제 이동 좌표 계산 및 브로드캐스팅...
        return Task.FromResult(Response.Ok(new MoveRes()));
    }
}
```

---

## 6. 구현 설계 방향성 요약 (Implementation Guidelines)

### 6.1 Actor 생성 및 Attribute 기반 권한 제어 통합
**파일**: `SimpleNetEngine.Game/Middleware/` 영역, 혹은 `MessageDispatcher` 구현체 내부

부팅(서버 구동) 시점에 패킷별 요구 Attribute를 미리 캐싱하고, 런타임에 성능 하락 없이 즉시 검증합니다.

```csharp
public async ValueTask ExecuteAsync(PacketContext context, NextDelegate next)
{
    // 현재 세션의 Actor 관할 상태 획득 (매핑이 없으면 None)
    var actorState = context.SessionActor?.State ?? ActorState.None;

    // Reflection을 통해 만들어진 핸들러 메타데이터 캐시에서 권한 조건 도출
    var handlerMeta = _handlerCache.Get(context.MessageId);
    
    if (handlerMeta.RequireState.HasValue && !handlerMeta.RequireState.Value.HasFlag(actorState))
    {
        _logger.LogWarning("권한 부족 및 비정상 패킷 접근: MessageId={MsgId}, 현재상태={Current}, 요구상태={Req}", 
            context.MessageId, actorState, handlerMeta.RequireState);
        context.Response = CreateErrorResponse("UNAUTHORIZED");
        return; // 파이프라인 흐름 차단
    }

    await next(context);
}
```

### 6.2 분산 락을 활용한 스레드 안전 로그인 진입
**파일**: `SimpleNetEngine.Game/Core/GameServerHub.cs` 또는 로그인 라우팅 핵심부

동일 계정에 대한 분산공격급의 동시 로그인 트래픽(T3 버그)을 완벽히 차단하기 위해 동기화 진입단에 `TryAcquireLockAsync` 분산 락을 걸어서 단일 진입을 보장받도록 처리합니다.

```csharp
private async Task HandleLoginReq(PacketContext context)
{
    var (userId, isReconnect, sequenceId) = ExtractLoginInfo(context.Payload);

    // SequenceId 검증 (ActorState.Created 단계에서 발급받은 랜덤값과 불일치시 방어)
    if (context.SessionActor.SequenceId != sequenceId) { return; }

    // 분산 락 기법 도입 (복수의 동시 로그인 시도 완벽 차단)
    await using var lockObj = await _redisDb.TryAcquireLockAsync($"login:{userId}", expirySeconds: 10);
    if (lockObj == null)
    {
        context.Response = CreateErrorResponse("LOGIN_BUSY");
        return;
    }

    try 
    {
        // 상태를 임시로 Authenticating 으로 전환
        context.SessionActor.TransitionState(ActorState.Authenticating);
        
        var existingSession = await _sessionStore.GetSessionAsync(userId);
        if (existingSession != null)
        {
            await HandleDuplicateLogin(userId, existingSession, context); // 기존 처리
        }
        else 
        {
            await ProcessNewLogin(userId, context); // 신규 처리
        }
    }
    catch(Exception ex) 
    {
        // 네트워크 끊김이나 Redis 타임아웃 등 예외 발생시 롤백 로직 보장
        _logger.LogError(ex, "로그인 비즈니스 처리 중 치명적 실패");
        context.SessionActor.TransitionState(ActorState.Created); 
        context.Response = CreateErrorResponse("LOGIN_ERROR");
    }
}
```

### 6.3 Gateway 단의 즉각 선점 라우팅 처리
**파일**: `SimpleNetEngine.Gateway/Network/GatewaySession.cs`

사용자가 TCP 접속의 암호화 핸드쉐이크를 완료하자마자, 어떠한 정보 전송도 기다리지 않고 즉각적으로 타겟 GameServer 노드를 고정(Pin)합니다.

```csharp
public override void OnConnected()
{
    base.OnConnected();
    
    // 1. 임의 GameServer 노드를 선택 (Round-robin 로직 활용)하고 즉시 고정
    _pinnedNodeId = _packetRouter.GetNextGameServerNodeId();
    _sessionId = 0; // 아직 Actor 인증 전이므로 SessionId 미할당 익명 상태
    
    // 2. 타겟 GameServer에 접속을 즉각 통보시켜 빈 깡통 Actor가 준비되도록 유도
    _packetRouter.ForwardConnectionNotification(_socketId, _pinnedNodeId);
}
```

---

## 7. 아키텍처 원칙 준수 요약정리 (Architecture Compliance Check)

본 개편안은 현재 시스템의 전체적인 코어 설계 철학을 준수하며 모순된 부분을 배제합니다.

- **Gateway as Dumb Proxy**: Gateway 자체에 `SequenceId` 발급이나 비즈니스 캐시 로직을 붙이지 않고, 최초 분배 통보 및 고정(Pinning) 로드 밸런서의 기본 역할만을 철저히 수행하도록 유도했습니다. 실제 Actor 생성과 관리 등은 GameServer에 위임됩니다.
- **GameServer as Smart Hub**: Redis 통신을 위한 분산 락(Redis Distributed Lock) 획득, 실질적 사용자 인증, Actor 생명주기 트랜지션 및 시퀀스아이디 보안 발급은 오롯이 GameServer 내부의 단독 흐름(Isolation)에서 보장받아 100% 분산 제어됩니다.
- **Race Condition 원천 차단 철학**: `Task.Delay`나 백그라운드 스레드의 RPC 완료를 단순히 운에 맡겨 기다리던 기존 방식을 완전히 퇴출하고, Gateway 단계의 즉시 핀 선점, GameServer 단계의 Awaited 처리로 모든 데이터 스트림 병목을 일원화 하였습니다.
