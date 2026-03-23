using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SimpleNetEngine.Game.Middleware;

/// <summary>
/// 성능 측정 Middleware
/// Stopwatch 할당 없이 Stopwatch.GetTimestamp() 기반 측정
/// </summary>
public class PerformanceMiddleware(ILogger<PerformanceMiddleware> logger) : IPacketMiddleware
{
    private const long SlowPacketThresholdTicks = 100 * TimeSpan.TicksPerMillisecond;

    public async Task InvokeAsync(PacketContext context, Func<Task> next)
    {
        var start = Stopwatch.GetTimestamp();

        await next();

        var elapsed = Stopwatch.GetElapsedTime(start);
        if (elapsed.Ticks > SlowPacketThresholdTicks)
        {
            logger.LogWarning("Slow packet: Session={SessionId}, Elapsed={ElapsedMs:F1}ms",
                context.SessionId, elapsed.TotalMilliseconds);
        }
    }
}
