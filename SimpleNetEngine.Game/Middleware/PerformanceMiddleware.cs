using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SimpleNetEngine.Game.Middleware;

/// <summary>
/// 성능 측정 Middleware
/// 패킷 처리 시간 측정 및 통계 (AOP - Performance Monitoring)
/// </summary>
public class PerformanceMiddleware : IPacketMiddleware
{
    private readonly ILogger<PerformanceMiddleware> _logger;
    private const int SlowPacketThresholdMs = 100; // 100ms 이상이면 slow로 간주

    public PerformanceMiddleware(ILogger<PerformanceMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task InvokeAsync(PacketContext context, Func<Task> next)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 다음 Middleware 실행
            await next();
        }
        finally
        {
            stopwatch.Stop();

            var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;

            // Context에 실행 시간 저장
            context.Items["ElapsedMs"] = elapsedMs;

            // Slow packet 경고
            if (elapsedMs > SlowPacketThresholdMs)
            {
                _logger.LogWarning("Slow packet detected: Session={SessionId}, Opcode={Opcode}, Elapsed={ElapsedMs}ms",
                    context.SessionId, context.Opcode, elapsedMs);
            }
            else
            {
                _logger.LogTrace("Packet processed: Elapsed={ElapsedMs}ms", elapsedMs);
            }

            // TODO: 메트릭 수집 (Prometheus, StatsD 등)
            // _metricsCollector.RecordPacketProcessingTime(context.Opcode, elapsedMs);
        }
    }
}
