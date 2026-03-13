using Microsoft.Extensions.Logging;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Game.Middleware;

/// <summary>
/// SequenceId 검증 Middleware (Replay Attack 방어)
/// 클라이언트가 보내는 SequenceId가 단조 증가하는지 확인하여 중복/재전송 패킷을 차단한다.
/// SequenceId == 0은 유효하지 않음 — 모든 패킷에 유효한 SequenceId 필수.
/// </summary>
public class SequenceIdMiddleware : IPacketMiddleware
{
    private readonly ILogger<SequenceIdMiddleware> _logger;

    public SequenceIdMiddleware(ILogger<SequenceIdMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task InvokeAsync(PacketContext context, Func<Task> next)
    {
        if (context.Items.TryGetValue("Actor", out var actorObj) && actorObj is SessionActor actor)
        {
            if (!actor.ValidateAndUpdateSequenceId(context.SequenceId))
            {
                _logger.LogWarning(
                    "Invalid SequenceId (replay attack?): SessionId={SessionId}, SequenceId={SequenceId}, Last={LastSequenceId}",
                    context.SessionId, context.SequenceId, actor.LastClientSequenceId);

                context.Response = Response.Error((short)ErrorCode.GameDuplicateSequenceId);
                context.IsCompleted = true;
                return;
            }
        }

        await next();
    }
}
