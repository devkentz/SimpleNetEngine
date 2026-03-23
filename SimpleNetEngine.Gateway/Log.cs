using Microsoft.Extensions.Logging;

namespace SimpleNetEngine.Gateway;

/// <summary>
/// Gateway Hot Path LoggerMessage (Zero-Alloc, Source-Generated)
/// IsEnabled 체크가 source generator에 의해 자동 삽입되어
/// 레벨 미달 시 인자 평가 및 할당 없이 즉시 반환됩니다.
/// </summary>
internal static partial class Log
{
    // ─── GatewaySession ───

    [LoggerMessage(Level = LogLevel.Debug, Message = "SendFromGameServer: SocketId={SocketId}, PacketSize={Size}, IsConnected={IsConnected}")]
    internal static partial void SendFromGameServer(ILogger logger, Guid socketId, int size, bool isConnected);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send packet to client: SocketId={SocketId}, SessionId={SessionId}, PacketSize={Size}")]
    internal static partial void SendToClientFailed(ILogger logger, Guid socketId, long sessionId, int size);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid packet: offset={Offset}, size={Size}, SocketId={SocketId}")]
    internal static partial void InvalidPacket(ILogger logger, long offset, long size, Guid socketId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Packet boundary violation: offset={Offset}, size={Size}, bufferLength={BufferLength}, SocketId={SocketId}")]
    internal static partial void PacketBoundaryViolation(ILogger logger, long offset, long size, int bufferLength, Guid socketId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid TotalLength: {TotalLength}, SocketId={SocketId}")]
    internal static partial void InvalidTotalLength(ILogger logger, int totalLength, Guid socketId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit exceeded: SocketId={SocketId}, Count={Count}/sec")]
    internal static partial void RateLimitExceeded(ILogger logger, Guid socketId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Packet dropped (not ready): SocketId={SocketId}, NodeId={NodeId}, SessionId={SessionId}, PacketSize={PacketSize}")]
    internal static partial void PacketDroppedNotReady(ILogger logger, Guid socketId, long nodeId, long sessionId, int packetSize);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Encrypted packet but no key: SocketId={SocketId}")]
    internal static partial void EncryptedNoKey(ILogger logger, Guid socketId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Decryption failed: SocketId={SocketId}, SessionId={SessionId}")]
    internal static partial void DecryptionFailed(ILogger logger, Guid socketId, long sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Decompression failed: SocketId={SocketId}, SessionId={SessionId}")]
    internal static partial void DecompressionFailed(ILogger logger, Guid socketId, long sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send error packet to client: SocketId={SocketId}, ErrorCode={ErrorCode}")]
    internal static partial void SendErrorFailed(ILogger logger, Guid socketId, short errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kick: SocketId={SocketId}, Reason={Reason}, SessionId={SessionId}")]
    internal static partial void Kick(ILogger logger, Guid socketId, SimpleNetEngine.Protocol.Packets.ErrorCode reason, long sessionId);

    // ─── GamePacketRouter ───

    [LoggerMessage(Level = LogLevel.Debug, Message = "HandleServerPacket: SessionId={SessionId}, PayloadSize={Size}")]
    internal static partial void HandleServerPacket(ILogger logger, long sessionId, int size);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client session not found: SessionId={SessionId}")]
    internal static partial void SessionNotFound(ILogger logger, long sessionId);
}
