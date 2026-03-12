using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Game.Actor;

namespace SimpleNetEngine.Game.Services;

/// <summary>
/// ActorDisposeQueue를 주기적으로 Drain하여 Actor를 안전하게 Dispose하는 BackgroundService.
/// Actor mailbox 내부에서 self-deadlock 없이 Dispose를 수행한다.
/// </summary>
public class ActorDisposeService(
    ActorDisposeQueue disposeQueue,
    ILogger<ActorDisposeService> logger) : BackgroundService
{
    private static readonly TimeSpan DrainInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ActorDisposeService started: DrainInterval={Interval}s", DrainInterval.TotalSeconds);

        using var timer = new PeriodicTimer(DrainInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var disposed = disposeQueue.DrainAndDispose();
            if (disposed > 0)
            {
                logger.LogDebug("ActorDisposeService: Disposed {Count} actor(s)", disposed);
            }
        }
    }
    
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        var remaining = disposeQueue.DrainAndDispose();
        if (remaining > 0)
            logger.LogDebug("ActorDisposeService shutdown: Disposed {Count} remaining actor(s)", remaining);
    }
}
