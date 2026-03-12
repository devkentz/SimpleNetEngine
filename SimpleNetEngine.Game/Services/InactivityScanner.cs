using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Network;
using SimpleNetEngine.Game.Session;
using SimpleNetEngine.Game.Options;

namespace SimpleNetEngine.Game.Services;

/// <summary>
/// Actor 생명주기 스캐너 BackgroundService.
///
/// 두 가지 역할을 통합:
/// 1. Active Actor 비활성 감지: InactivityTimeout 초과 시 Disconnect 처리
/// 2. Disconnected Actor Grace Period 만료: ReconnectGracePeriod 초과 시 cleanup (OnLogoutAsync → Redis 삭제 → Actor 제거)
///
/// 스캔 간격은 두 타임아웃 중 짧은 값의 1/3 (최소 1초).
/// </summary>
public class InactivityScanner(
    ISessionActorManager actorManager,
    ActorDisconnectHandler disconnectHandler,
    GatewayDisconnectQueue disconnectQueue,
    IServiceScopeFactory scopeFactory,
    IOptions<GameOptions> options,
    ILogger<InactivityScanner> logger) : BackgroundService
{
    private readonly TimeSpan _inactivityTimeout = options.Value.InactivityTimeout;
    private readonly TimeSpan _gracePeriod = options.Value.ReconnectGracePeriod;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_inactivityTimeout <= TimeSpan.Zero && _gracePeriod <= TimeSpan.Zero)
        {
            logger.LogInformation("InactivityScanner disabled (both InactivityTimeout and ReconnectGracePeriod <= 0)");
            return;
        }

        // 스캔 간격: 활성 타임아웃 중 짧은 값의 1/3 (충분한 감지 해상도)
        var minTimeout = GetMinPositiveTimeout();
        var scanInterval = TimeSpan.FromTicks(minTimeout.Ticks / 3);
        if (scanInterval < TimeSpan.FromSeconds(1))
            scanInterval = TimeSpan.FromSeconds(1);

        logger.LogInformation(
            "InactivityScanner started: InactivityTimeout={InactivityTimeout}s, GracePeriod={GracePeriod}s, ScanInterval={Interval}s",
            _inactivityTimeout.TotalSeconds, _gracePeriod.TotalSeconds, scanInterval.TotalSeconds);

        using var timer = new PeriodicTimer(scanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            ScanActors();
        }
    }

    private TimeSpan GetMinPositiveTimeout()
    {
        if (_inactivityTimeout <= TimeSpan.Zero) return _gracePeriod;
        if (_gracePeriod <= TimeSpan.Zero) return _inactivityTimeout;
        return _inactivityTimeout < _gracePeriod ? _inactivityTimeout : _gracePeriod;
    }

    private void ScanActors()
    {
        var now = Stopwatch.GetTimestamp();
        var inactiveCount = 0;
        var expiredCount = 0;

        foreach (var actor in actorManager.GetAllActors())
        {
            switch (actor.Status)
            {
                case ActorState.Active when _inactivityTimeout > TimeSpan.Zero:
                {
                    var elapsed = Stopwatch.GetElapsedTime(actor.LastActivityTicks, now);
                    if (elapsed <= _inactivityTimeout)
                        continue;

                    logger.LogWarning(
                        "Inactivity detected: SessionId={SessionId}, UserId={UserId}, Idle={IdleSeconds}s",
                        actor.ActorId, actor.UserId, elapsed.TotalSeconds);

                    _ = DisconnectClientAsync(actor);
                    inactiveCount++;
                    break;
                }

                case ActorState.Disconnected when _gracePeriod > TimeSpan.Zero:
                {
                    var disconnectedTicks = actor.DisconnectedTicks;
                    if (disconnectedTicks == 0)
                        continue;

                    var elapsed = Stopwatch.GetElapsedTime(disconnectedTicks, now);
                    if (elapsed <= _gracePeriod)
                        continue;

                    logger.LogInformation(
                        "Grace period expired: SessionId={SessionId}, UserId={UserId}, Disconnected={DisconnectedSeconds}s",
                        actor.ActorId, actor.UserId, elapsed.TotalSeconds);

                    _ = ExpireGracePeriodAsync(actor);
                    expiredCount++;
                    break;
                }
            }
        }

        if (inactiveCount > 0)
        {
            logger.LogInformation(
                "InactivityScanner: {Count} client(s) disconnected due to inactivity",
                inactiveCount);
        }

        if (expiredCount > 0)
        {
            logger.LogInformation(
                "InactivityScanner: {Count} actor(s) cleaned up after grace period expiry",
                expiredCount);
        }
    }

    private async Task DisconnectClientAsync(ISessionActor actor)
    {
        var sessionId = actor.ActorId;
        var gatewayNodeId = actor.GatewayNodeId;

        try
        {
            await actor.ExecuteAsync(async _ =>
            {
                if (actor.Status != ActorState.Active)
                    return;

                using var scope = scopeFactory.CreateScope();
                var loginHandler = scope.ServiceProvider.GetRequiredService<ILoginHandler>();

                var action = await loginHandler.OnInactivityTimeoutAsync(actor);

                if (action == DisconnectAction.AllowSessionResume)
                {
                    await disconnectHandler.AllowSessionResumeAsync(actor, loginHandler);
                }
                else
                {
                    await disconnectHandler.TerminateSessionAsync(actor, loginHandler);
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to transition actor via mailbox: SessionId={SessionId}",
                sessionId);
            return;
        }

        // Gateway 소켓 해제를 대기열에 예약
        disconnectQueue.Schedule(sessionId, gatewayNodeId);
    }

    private async Task ExpireGracePeriodAsync(ISessionActor actor)
    {
        var sessionId = actor.ActorId;

        try
        {
            await actor.ExecuteAsync(async _ =>
            {
                if (actor.Status != ActorState.Disconnected)
                    return;

                using var scope = scopeFactory.CreateScope();
                var loginHandler = scope.ServiceProvider.GetRequiredService<ILoginHandler>();

                await disconnectHandler.ExpireGracePeriodAsync(actor, loginHandler);
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to expire grace period via mailbox: SessionId={SessionId}",
                sessionId);
        }
    }
}
