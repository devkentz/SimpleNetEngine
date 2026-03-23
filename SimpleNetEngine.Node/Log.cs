using Microsoft.Extensions.Logging;

namespace SimpleNetEngine.Node;

/// <summary>
/// Node Hot Path LoggerMessage (Zero-Alloc, Source-Generated)
/// </summary>
internal static partial class Log
{
    // ─── NodeDispatcher ───

    [LoggerMessage(Level = LogLevel.Warning, Message = "NodeDispatcher: No handler for MsgId={MsgId}")]
    internal static partial void NoHandlerForMsgId(ILogger logger, int msgId);

}
