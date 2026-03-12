using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Session;

namespace SimpleNetEngine.Game.Services;

/// <summary>
/// Actor disconnect/cleanup 공통 핸들러.
/// AllowSessionResume(Disconnected + Grace Period) / TerminateSession(즉시 제거)
/// 두 가지 정책을 캡슐화하여 중복 코드를 제거한다.
///
/// 호출자: ConnectionController, InactivityScanner, KickoutController
/// 반드시 actor.ExecuteAsync (mailbox context) 내부에서 호출할 것.
/// </summary>
public class ActorDisconnectHandler(
    ISessionActorManager actorManager,
    ActorDisposeQueue disposeQueue,
    ISessionStore sessionStore,
    IServiceScopeFactory scopeFactory,
    ILogger<ActorDisconnectHandler> logger)
{
    /// <summary>
    /// AllowSessionResume: Disconnected 상태 전이 + Grace Period 시작.
    /// Grace Period 만료 시 OnLogoutAsync → Redis 삭제 → Actor 제거.
    ///
    /// 반드시 actor.ExecuteAsync 내부에서 호출할 것 (mailbox 스레드 안전성).
    /// </summary>
    public async Task AllowSessionResumeAsync(
        ISessionActor actor,
        ILoginHandler loginHandler,
        TimeSpan gracePeriod)
    {
        if (actor.Status is ActorState.Disposed or ActorState.Disconnected)
            return;

        actor.Status = ActorState.Disconnected;
        var sessionId = actor.ActorId;
        var userId = actor.UserId;

        logger.LogInformation(
            "Actor transitioned to Disconnected (AllowSessionResume): SessionId={SessionId}, UserId={UserId}",
            sessionId, userId);

        await loginHandler.OnDisconnectedAsync(actor);

        actor.StartGracePeriod(gracePeriod, async () =>
        {
            logger.LogInformation(
                "Grace period expired: SessionId={SessionId}, UserId={UserId}",
                sessionId, userId);

            // ILoginHandler는 Scoped — Grace Period 콜백은 원래 스코프 밖이므로 새 스코프 생성
            using var scope = scopeFactory.CreateScope();
            var graceLoginHandler = scope.ServiceProvider.GetRequiredService<ILoginHandler>();

            await graceLoginHandler.OnLogoutAsync(actor);
            await sessionStore.DeleteReconnectKeyAsync(actor.ReconnectKey);
            var result = actorManager.UnregisterActor(sessionId);
            if (result.IsSuccess)
                disposeQueue.Enqueue(result.Value);

            // 조건부 삭제: 내 SessionId와 일치할 때만 삭제 (다른 노드가 덮어쓴 경우 보호)
            await sessionStore.DeleteSessionIfMatchAsync(userId, sessionId);
        });
    }

    /// <summary>
    /// TerminateSession: 즉시 Actor 제거 + Redis 정리.
    ///
    /// 반드시 actor.ExecuteAsync 내부에서 호출할 것 (mailbox 스레드 안전성).
    /// Redis 세션 삭제는 조건부: 내 SessionId와 일치할 때만 삭제 (다른 노드가 덮어쓴 경우 보호).
    /// </summary>
    public async Task TerminateSessionAsync(
        ISessionActor actor,
        ILoginHandler loginHandler)
    {
        var sessionId = actor.ActorId;
        var userId = actor.UserId;

        logger.LogInformation(
            "Actor terminated (TerminateSession): SessionId={SessionId}, UserId={UserId}",
            sessionId, userId);

        await loginHandler.OnLogoutAsync(actor);

        await sessionStore.DeleteReconnectKeyAsync(actor.ReconnectKey);

        var result = actorManager.UnregisterActor(sessionId);
        if (result.IsSuccess)
            disposeQueue.Enqueue(result.Value);

        // 조건부 삭제: 내 SessionId와 일치할 때만 삭제 (다른 노드가 덮어쓴 경우 보호)
        await sessionStore.DeleteSessionIfMatchAsync(userId, sessionId);
    }
}
