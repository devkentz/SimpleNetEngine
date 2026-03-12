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
/// 비활성 클라이언트 감지 BackgroundService.
/// 주기적으로 모든 Active Actor를 스캔하여 InactivityTimeout을 초과한 Actor를 감지하고,
/// ILoginHandler.OnInactivityTimeoutAsync의 DisconnectAction에 따라
/// ActorDisconnectHandler에 위임한다.
/// 이후 GatewayDisconnectQueue에 소켓 해제를 예약한다.
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
        if (_inactivityTimeout <= TimeSpan.Zero)
        {
            logger.LogInformation("InactivityScanner disabled (InactivityTimeout <= 0)");
            return;
        }

        // 스캔 간격: InactivityTimeout / 3 (충분한 감지 해상도)
        var scanInterval = TimeSpan.FromTicks(_inactivityTimeout.Ticks / 3);
        if (scanInterval < TimeSpan.FromSeconds(1))
            scanInterval = TimeSpan.FromSeconds(1);

        logger.LogInformation(
            "InactivityScanner started: Timeout={Timeout}s, ScanInterval={Interval}s",
            _inactivityTimeout.TotalSeconds, scanInterval.TotalSeconds);

        using var timer = new PeriodicTimer(scanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            ScanInactiveActors();
        }
    }

    private void ScanInactiveActors()
    {
        var now = Stopwatch.GetTimestamp();
        var disconnectedCount = 0;

        foreach (var actor in actorManager.GetAllActors())
        {
            if (actor.Status != ActorState.Active)
                continue;

            var elapsed = Stopwatch.GetElapsedTime(actor.LastActivityTicks, now);
            if (elapsed <= _inactivityTimeout)
                continue;

            logger.LogWarning(
                "Inactivity detected: SessionId={SessionId}, UserId={UserId}, Idle={IdleSeconds}s",
                actor.ActorId, actor.UserId, elapsed.TotalSeconds);

            _ = DisconnectClientAsync(actor);
            disconnectedCount++;
        }

        if (disconnectedCount > 0)
        {
            logger.LogInformation(
                "InactivityScanner: {Count} client(s) disconnected due to inactivity",
                disconnectedCount);
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
                    await disconnectHandler.AllowSessionResumeAsync(actor, loginHandler, _gracePeriod);
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
}
