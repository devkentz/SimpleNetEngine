using System.Buffers.Binary;
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
        if (!NetHeaderHelper.TryRead<EndPointHeader>(span, out var endPointHeader))
            throw new InvalidOperationException($"Payload too small: {span.Length} bytes");

        if(span.Length < endPointHeader.TotalLength)
            throw new InvalidOperationException($"Payload too small: {span.Length} bytes");

        var afterEp = NetHeaderHelper.GetPayload<EndPointHeader>(span);

        if (!NetHeaderHelper.TryRead<GameHeader>(afterEp, out var header))
            throw new InvalidOperationException($"Payload too small: {span.Length} bytes");

        // 나머지는 Protobuf payload
        var protoPayload = NetHeaderHelper.GetPayload<GameHeader>(afterEp);
        var message = parser.ParseFrom(protoPayload);

        return (header, message);
    }
}
