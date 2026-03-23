using Microsoft.Extensions.Logging;
using SimpleNetEngine.Game.Actor;

namespace SimpleNetEngine.Game;

/// <summary>
/// Game Hot Path LoggerMessage (Zero-Alloc, Source-Generated)
/// </summary>
internal static partial class Log
{
    // ─── GameServerHub ───

    [LoggerMessage(Level = LogLevel.Warning, Message = "Actor not found: SessionId={SessionId}. Sending SESSION_EXPIRED.")]
    internal static partial void ActorNotFound(ILogger logger, long sessionId);

    // ─── MessageDispatcher ───

    [LoggerMessage(Level = LogLevel.Warning, Message = "ActorState rejected: ActorId={ActorId}, MsgId={MsgId}, Current={Current}")]
    internal static partial void ActorStateRejected(ILogger logger, long actorId, int msgId, ActorState current);
}
