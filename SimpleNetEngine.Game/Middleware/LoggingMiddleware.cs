using Microsoft.Extensions.Logging;

namespace SimpleNetEngine.Game.Middleware;

/// <summary>
/// 로깅 Middleware
/// 패킷 수신/처리 로깅 (AOP - Cross-cutting Concern)
/// </summary>
public class LoggingMiddleware : IPacketMiddleware
{
    private readonly ILogger<LoggingMiddleware> _logger;

    public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task InvokeAsync(PacketContext context, Func<Task> next)
    {
        // Request 로깅
        _logger.LogDebug(
            "Packet received: Gateway={GatewayNodeId}, Session={SessionId}, Size={Size}",
            context.GatewayNodeId, context.SessionId, context.Payload.Length);

        // 다음 Middleware 실행
        await next();

        // Response 로깅
        if (context.Response != null)
        {
            _logger.LogDebug(
                "Packet response ready: Session={SessionId}, MsgType={MsgType}",
                context.SessionId, context.Response.Message?.GetType().Name);
        }

        // 예외 발생 시 로깅
        if (context.Exception != null)
        {
            _logger.LogWarning(
                "Packet processing failed: Session={SessionId}, Error={Error}",
                context.SessionId, context.Exception.Message);
        }
    }
}
