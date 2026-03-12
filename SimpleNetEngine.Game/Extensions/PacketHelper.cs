using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Google.Protobuf;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Game.Extensions;

/// <summary>
/// 패킷 파싱 헬퍼
/// 응답 직렬화는 GameSessionChannelListener.SendResponse에서 Msg에 직접 Zero-Copy로 수행
/// </summary>
public static class PacketHelper
{
    /// <summary>
    /// 클라이언트 패킷에서 Header와 Protobuf 메시지를 파싱
    /// Wire format: [EndPointHeader(4)][GameHeader(8)][Protobuf Payload...]
    /// </summary>
    public static (GameHeader header, IMessage message) ParseClientPacket(ReadOnlySpan<byte> span, MessageParser parser)
    {
        if (span.Length < EndPointHeader.Size + GameHeader.Size)
            throw new InvalidOperationException($"Payload too small: {span.Length} bytes");

        int offset = 0;
        var totalSize = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        if(span.Length < totalSize)
            throw new InvalidOperationException($"Payload too small: {span.Length} bytes");

        // EndPointHeader
        var endPointHeader = MemoryMarshal.Read<EndPointHeader>(span[offset..]);
        offset += EndPointHeader.Size;

        // GameHeader
        var header = MemoryMarshal.Read<GameHeader>(span[offset..]);
        offset += GameHeader.Size;

        // 나머지는 Protobuf payload
        var protoPayload = span[offset..];
        var message = parser.ParseFrom(protoPayload);

        return (header, message);
    }
}
