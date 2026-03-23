using Game.Protocol;
using Internal.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Extensions;
using SimpleNetEngine.Game.Network;
using SimpleNetEngine.Game.Options;
using SimpleNetEngine.Game.Session;
using SimpleNetEngine.Infrastructure.DistributeLock;
using SimpleNetEngine.Node.Network;
using SimpleNetEngine.ProtoGenerator;
using SimpleNetEngine.Protocol.Packets;
using StackExchange.Redis;

namespace SimpleNetEngine.Game.Controllers;

/// <summary>
/// 라이브러리 내장 LoginGameReq 핸들러
/// HandshakeReq 이후 Authenticating 상태의 Actor에서 실행됨
///
/// 3가지 시나리오:
/// 1. 신규 로그인: Redis에 세션 없음 → 세션 등록 + Actor 활성화
/// 2. 동일 노드 중복/재접속: 기존 Actor 정리 + 새 세션 등록
/// 3. 다른 노드 중복: Kickout RPC + 새 세션 등록
///
/// ILoginHandler를 통해 앱이 인증 로직과 Hook을 주입한다.
/// </summary>
public class LoginController(
    ILogger<LoginController> logger,
    ISessionStore sessionStore,
    ISessionActorManager actorManager,
    ActorDisposeQueue disposeQueue,
    GatewayDisconnectQueue disconnectQueue,
    KickoutMessageHandler kickoutHandler,
    INodeSender nodeSender,
    ILoginHandler loginHandler,
    IDatabase redis,
    IClientSender clientSender,
    IOptions<GameOptions> options,
    TimeProvider timeProvider)
{
    private readonly GameOptions _options = options.Value;

    /// <summary>
    /// LoginGameReq 처리: credential → ILoginHandler.AuthenticateAsync → Redis 조회 → 분기
    /// </summary>
    public async Task<Response> HandleLogin(ISessionActor actor, LoginGameReq req)
    {
        // ILoginHandler를 통한 인증 (앱이 credential 바이트를 자체 proto로 역직렬화)
        var authResult = await loginHandler.AuthenticateAsync(req.Credential.Memory, actor);

        if (!authResult.IsSuccess)
        {
            logger.LogWarning(
                "Authentication failed: ActorId={ActorId}, ErrorCode={ErrorCode}, Message={Message}",
                actor.ActorId, authResult.ErrorCode, authResult.ErrorMessage);

            return Response.Ok(new LoginGameRes
            {
                Success = false,
                ErrorMessage = authResult.ErrorMessage ?? "Authentication failed"
            });
        }

        var userId = authResult.UserId;

        logger.LogDebug(
            "LoginGameReq received: ActorId={ActorId}, UserId={UserId}",
            actor.ActorId, userId);

        // 분산 락: 동일 UserId 동시 로그인 방지
        await using var lockObj = await redis.TryAcquireLockAsync($"login:{userId}", expirySeconds: 10);
        if (lockObj == null)
        {
            logger.LogWarning(
                "Login lock acquisition failed: UserId={UserId}, ActorId={ActorId}",
                userId, actor.ActorId);

            return Response.Ok(new LoginGameRes
            {
                Success = false,
                ErrorMessage = "Another login is in progress"
            });
        }

        // Redis에서 기존 세션 조회
        var existingSession = await sessionStore.GetSessionAsync(userId);
        if (existingSession == null)
        {
            return await HandleNewLogin(actor, userId);
        }

        if (existingSession.GameServerNodeId == _options.GameNodeId)
        {
            return await HandleSameNodeDuplicate(actor, userId, existingSession);
        }

        return await HandleCrossNodeDuplicate(actor, userId, existingSession);
    }

    /// <summary>
    /// 신규 로그인: Redis에 기존 세션 없음
    /// </summary>
    private async Task<Response> HandleNewLogin(ISessionActor actor, long userId)
    {
        var sessionInfo = new SessionInfo
        {
            GameServerNodeId = _options.GameNodeId,
            SessionId = actor.ActorId,
            GatewayNodeId = actor.GatewayNodeId,
            CreatedAtUtc = timeProvider.GetUtcNow(),
            LastActivityUtc = timeProvider.GetUtcNow()
        };

        await sessionStore.SetSessionAsync(userId, sessionInfo);

        actor.UserId = userId;

        // 앱 Hook: 로그인 성공 후 초기화 (DB 로드, 게임 상태 설정)
        await loginHandler.OnLoginSuccessAsync(actor);

        actor.Status = ActorState.Active;
        var reconnectKey = actor.RegenerateReconnectKey();
        await sessionStore.SetReconnectKeyAsync(reconnectKey, userId);

        logger.LogDebug(
            "New login completed: UserId={UserId}, SessionId={SessionId}",
            userId, actor.ActorId);

        return Response.Ok(new LoginGameRes { Success = true, ReconnectKey = reconnectKey.ToString() }).UseEncrypt();
    }

    /// <summary>
    /// 동일 노드 중복 로그인: 기존 Actor 정리 후 새 세션 등록
    /// </summary>
    private async Task<Response> HandleSameNodeDuplicate(
        ISessionActor actor, long userId, SessionInfo existingSession)
    {
        logger.LogWarning(
            "Same-node duplicate login: UserId={UserId}, OldSessionId={OldSessionId}, NewSessionId={NewSessionId}",
            userId, existingSession.SessionId, actor.ActorId);

        // 기존 Actor에 Kickout Hook 호출 + 알림 전송 + 제거
        if (existingSession.SessionId != actor.ActorId)
        {
            var oldActorResult = actorManager.GetActor(existingSession.SessionId);
            if (oldActorResult.IsSuccess)
            {
                // 새 Actor의 mailbox 스레드에서 직접 호출하면 old Actor와 race condition 발생
                // old Actor의 mailbox에 푸시하여 순차 실행 보장
                // KickoutNtf도 mailbox 안에서 전송해야 SequenceId race condition 방지
                var oldActor = oldActorResult.Value;
                await oldActor.ExecuteAsync(async sp =>
                {
                    await loginHandler.OnKickoutAsync(oldActor, EKickoutReason.DuplicateLogin);
                    SendKickoutNtf(oldActor, EKickoutReason.DuplicateLogin);
                });
            }

            var removeResult = actorManager.RemoveActor(existingSession.SessionId);
            if (removeResult.IsFailure)
            {
                logger.LogWarning(
                    "Failed to remove old Actor: SessionId={SessionId}, Error={ErrorCode}",
                    existingSession.SessionId, removeResult.Error);
            }

            // 기존 소켓 연결 해제 지시
            await SendDisconnectClientRequestAsync(existingSession.GatewayNodeId, existingSession.SessionId);
        }

        // 새 세션 정보로 Redis 갱신
        var newSession = new SessionInfo
        {
            GameServerNodeId = _options.GameNodeId,
            SessionId = actor.ActorId,
            GatewayNodeId = actor.GatewayNodeId,
            CreatedAtUtc = timeProvider.GetUtcNow(),
            LastActivityUtc = timeProvider.GetUtcNow()
        };

        await sessionStore.SetSessionAsync(userId, newSession);

        actor.UserId = userId;

        // 앱 Hook: 로그인 성공 후 초기화
        await loginHandler.OnLoginSuccessAsync(actor);

        actor.Status = ActorState.Active;
        var reconnectKey = actor.RegenerateReconnectKey();
        await sessionStore.SetReconnectKeyAsync(reconnectKey, userId);

        logger.LogDebug(
            "Same-node duplicate login resolved: UserId={UserId}, NewSessionId={SessionId}",
            userId, actor.ActorId);

        return Response.Ok(new LoginGameRes { Success = true, ReconnectKey = reconnectKey.ToString() }).UseEncrypt();
    }

    /// <summary>
    /// 다른 노드 중복 로그인: Kickout RPC 후 새 세션 등록
    /// </summary>
    private async Task<Response> HandleCrossNodeDuplicate(
        ISessionActor actor, long userId, SessionInfo existingSession)
    {
        logger.LogWarning(
            "Cross-node duplicate login: UserId={UserId}, OldNode={OldNode}, NewNode={NewNode}",
            userId, existingSession.GameServerNodeId, _options.GameNodeId);

        // 기존 GameServer에 Kickout 요청
        var ack = await kickoutHandler.SendKickoutRequestAsync(
            existingSession.GameServerNodeId,
            userId,
            existingSession.SessionId,
            existingSession.GatewayNodeId);

        if (!ack.Success)
        {
            logger.LogWarning(
                "KickoutRequest failed: UserId={UserId}, ErrorCode={ErrorCode}. Proceeding with login.",
                userId, ack.ErrorCode);
        }

        // 새 세션 정보로 Redis 갱신
        var newSession = new SessionInfo
        {
            GameServerNodeId = _options.GameNodeId,
            SessionId = actor.ActorId,
            GatewayNodeId = actor.GatewayNodeId,
            CreatedAtUtc = timeProvider.GetUtcNow(),
            LastActivityUtc = timeProvider.GetUtcNow()
        };

        await sessionStore.SetSessionAsync(userId, newSession);

        actor.UserId = userId;

        // 앱 Hook: 로그인 성공 후 초기화
        await loginHandler.OnLoginSuccessAsync(actor);

        actor.Status = ActorState.Active;
        var reconnectKey = actor.RegenerateReconnectKey();
        await sessionStore.SetReconnectKeyAsync(reconnectKey, userId);

        logger.LogDebug(
            "Cross-node duplicate login resolved: UserId={UserId}, NewSessionId={SessionId}, KickoutSuccess={KickoutSuccess}",
            userId, actor.ActorId, ack.Success);

        return Response.Ok(new LoginGameRes { Success = true, ReconnectKey = reconnectKey.ToString() }).UseEncrypt();
    }

    /// <summary>
    /// LogoutReq 처리: OnLogoutAsync → Redis 삭제 → Actor 제거 → Gateway 소켓 해제
    /// </summary>
    public async Task<Response> HandleLogout(ISessionActor actor, LogoutReq req)
    {
        logger.LogDebug(
            "LogoutReq received: ActorId={ActorId}, UserId={UserId}",
            actor.ActorId, actor.UserId);

        // Disconnected 상태였으면 타임스탬프 초기화
        actor.ClearDisconnected();

        // 앱 Hook: 최종 데이터 저장
        await loginHandler.OnLogoutAsync(actor);

        // Redis 세션 + ReconnectKey 삭제
        await sessionStore.DeleteReconnectKeyAsync(actor.ReconnectKey);
        await sessionStore.DeleteSessionAsync(actor.UserId);

        // Actor를 딕셔너리에서 제거 (Dispose는 DisposeQueue에 위임)
        // Actor mailbox 안에서 실행되므로 RemoveActor(Dispose 포함)는 self-deadlock 발생
        var unregResult = actorManager.UnregisterActor(actor.ActorId);
        if (unregResult.IsSuccess)
            disposeQueue.Enqueue(unregResult.Value);

        // Gateway 소켓 해제를 대기열에 예약.
        // 즉시 전송하면 LogoutRes보다 disconnect가 먼저 도착하는 race 발생.
        // GatewayDisconnectService가 주기적으로 drain하여 전송.
        // 클라이언트가 자연스럽게 TCP를 끊으면 ClientDisconnectedNtf에서 예약 취소.
        disconnectQueue.Schedule(actor.ActorId, actor.GatewayNodeId);

        return Response.Ok(new LogoutRes { Success = true });
    }

    /// <summary>
    /// 기존 클라이언트에 KickoutNtf 전송 (GSC 경유, Actor 제거 전)
    /// </summary>
    private void SendKickoutNtf(ISessionActor actor, EKickoutReason reason)
    {
        clientSender.SendNtf(actor, Response.Ntf(new KickoutNtf { Reason = reason }));
    }

    /// <summary>
    /// Gateway에 기존 소켓 연결 해제 요청 (Service Mesh RPC)
    /// </summary>
    private async Task SendDisconnectClientRequestAsync(long gatewayNodeId, long sessionId)
    {
        try
        {
            var req = new ServiceMeshDisconnectClientReq
            {
                GatewayNodeId = gatewayNodeId,
                SessionId = sessionId
            };

            await nodeSender.RequestAsync<ServiceMeshDisconnectClientReq, ServiceMeshDisconnectClientRes>(
                NodePacket.ServerActorId,
                gatewayNodeId,
                req);
        }
        catch (TimeoutException ex)
        {
            logger.LogError(ex, "DisconnectClientReq timed out: Gateway={NodeId}", gatewayNodeId);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "DisconnectClientReq failed: Gateway={NodeId}", gatewayNodeId);
        }
    }

    /// <summary>
    /// MessageDispatcher에 내장 핸들러를 수동 등록
    /// </summary>
    public static void RegisterHandlers(MessageDispatcher dispatcher)
    {
        dispatcher.RegisterHandler(LoginGameReq.MsgId, async (sp, actor, payload) =>
        {
            var parser = AutoGeneratedParsers.GetParserById(LoginGameReq.MsgId);
            if (parser == null) return null;

            var (header, message) = PacketHelper.ParseClientPacket(payload.Span, parser);
            var controller = sp.GetRequiredService<LoginController>();
            return await controller.HandleLogin(actor, (LoginGameReq)message);
        }, [ActorState.Authenticating]);

        dispatcher.RegisterHandler(LogoutReq.MsgId, async (sp, actor, payload) =>
        {
            var parser = AutoGeneratedParsers.GetParserById(LogoutReq.MsgId);
            if (parser == null) return null;

            var (header, message) = PacketHelper.ParseClientPacket(payload.Span, parser);
            var controller = sp.GetRequiredService<LoginController>();
            return await controller.HandleLogout(actor, (LogoutReq)message);
        }, [ActorState.Active]);

        dispatcher.RegisterHandler(ReconnectReq.MsgId, async (sp, actor, payload) =>
        {
            var parser = AutoGeneratedParsers.GetParserById(ReconnectReq.MsgId);
            if (parser == null) return null;

            var (header, message) = PacketHelper.ParseClientPacket(payload.Span, parser);
            var controller = sp.GetRequiredService<ReconnectController>();
            return await controller.HandleReconnect(actor, (ReconnectReq)message);
        }, [ActorState.Authenticating]);
    }
}
