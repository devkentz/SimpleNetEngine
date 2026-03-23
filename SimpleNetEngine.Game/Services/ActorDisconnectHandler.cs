using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Session;

namespace SimpleNetEngine.Game.Services;

/// <summary>
/// Actor disconnect/cleanup 공통 핸들러.
/// AllowSessionResume(Disconnected + 타임스탬프 기록) / TerminateSession(즉시 제거)
/// 두 가지 정책을 캡슐화하여 중복 코드를 제거한다.
///
/// Grace Period 만료 판단은 InactivityScanner가 주기적 스캔으로 처리.
///
/// 호출자: ConnectionController, InactivityScanner, KickoutController
/// 반드시 actor.ExecuteAsync (mailbox context) 내부에서 호출할 것.
/// </summary>
public class ActorDisconnectHandler(
    ISessionActorManager actorManager,
    ActorDisposeQueue disposeQueue,
    ISessionStore sessionStore,
    ILogger<ActorDisconnectHandler> logger)
{
    /// <summary>
    /// AllowSessionResume: Disconnected 상태 전이 + 타임스탬프 기록.
    /// Grace Period 만료 판단은 InactivityScanner가 주기적 스캔으로 처리.
    ///
    /// 반드시 actor.ExecuteAsync 내부에서 호출할 것 (mailbox 스레드 안전성).
    /// </summary>
    public async Task AllowSessionResumeAsync(
        ISessionActor actor,
        ILoginHandler loginHandler)
    {
        if (actor.Status is ActorState.Disposed or ActorState.Disconnected)
            return;

        actor.Status = ActorState.Disconnected;
        actor.MarkDisconnected();
        var sessionId = actor.ActorId;
        var userId = actor.UserId;

        logger.LogDebug(
            "Actor transitioned to Disconnected (AllowSessionResume): SessionId={SessionId}, UserId={UserId}",
            sessionId, userId);

        await loginHandler.OnDisconnectedAsync(actor);
    }

    /// <summary>
    /// Grace Period 만료 시 cleanup 실행.
    /// InactivityScanner가 actor.ExecuteAsync 내부에서 호출.
    /// OnLogoutAsync → Redis 삭제 → Actor 제거.
    /// </summary>
    public Task ExpireGracePeriodAsync(
        ISessionActor actor,
        ILoginHandler loginHandler)
    {
        logger.LogDebug(
            "Grace period expired: SessionId={SessionId}, UserId={UserId}",
            actor.ActorId, actor.UserId);

        return CleanupActorInternalAsync(actor, loginHandler);
    }

    /// <summary>
    /// TerminateSession: 즉시 Actor 제거 + Redis 정리.
    ///
    /// 반드시 actor.ExecuteAsync 내부에서 호출할 것 (mailbox 스레드 안전성).
    /// Redis 세션 삭제는 조건부: 내 SessionId와 일치할 때만 삭제 (다른 노드가 덮어쓴 경우 보호).
    /// </summary>
    public Task TerminateSessionAsync(
        ISessionActor actor,
        ILoginHandler loginHandler)
    {
        logger.LogDebug(
            "Actor terminated (TerminateSession): SessionId={SessionId}, UserId={UserId}",
            actor.ActorId, actor.UserId);

        return CleanupActorInternalAsync(actor, loginHandler);
    }

    private async Task CleanupActorInternalAsync(
        ISessionActor actor,
        ILoginHandler loginHandler)
    {
        await loginHandler.OnLogoutAsync(actor);
        await sessionStore.DeleteReconnectKeyAsync(actor.ReconnectKey);

        var result = actorManager.UnregisterActor(actor.ActorId);
        if (result.IsSuccess)
            disposeQueue.Enqueue(result.Value);

        // 조건부 삭제: 내 SessionId와 일치할 때만 삭제 (다른 노드가 덮어쓴 경우 보호)
        await sessionStore.DeleteSessionIfMatchAsync(actor.UserId, actor.ActorId);
    }
}
