using Game.Protocol;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Game.Middleware;

/// <summary>
/// 예외 처리 Middleware
/// 모든 예외를 캐치하여 로깅하고 에러 응답 생성
/// </summary>
public class ExceptionHandlingMiddleware(ILogger<ExceptionHandlingMiddleware> logger) : IPacketMiddleware
{
    public async Task InvokeAsync(PacketContext context, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unhandled exception in packet processing: SessionId={SessionId}, UserId={UserId}",
                context.SessionId, context.UserId);

            context.Exception = ex;

            // Protobuf ErrorRes 응답 생성
            context.Response ??= Response.Error((ushort)ErrorCode.GameInternalError, new ErrorRes
            {
                ErrorCode = (int)ErrorCode.GameInternalError,
                ErrorMessage = "Internal server error"
            });

            // Zero-Copy 응답 전송
            if (context.SendResponse != null)
            {
                // Actor가 있으면 NextSequenceId 발급, 없으면 0
                ushort seqId = 0;
                if (context.Items.TryGetValue("Actor", out var actorObj) && actorObj is Actor.SessionActor actor)
                    seqId = (ushort)actor.NextSequenceId();

                context.SendResponse(
                    context.GatewayNodeId,
                    context.SessionId,
                    context.Response,
                    context.RequestId,
                    seqId);
            }

            context.IsCompleted = true;
        }
    }
}
