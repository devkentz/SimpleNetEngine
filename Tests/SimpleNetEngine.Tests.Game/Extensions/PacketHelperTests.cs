using FluentAssertions;
using Proto.Test;
using SimpleNetEngine.Game.Extensions;
using Google.Protobuf;
using SimpleNetEngine.Protocol.Packets;
using System.Runtime.InteropServices;

namespace SimpleNetEngine.Tests.Game.Extensions;

public class PacketHelperTests
{
    [Fact]
    public void ParseClientPacket_ValidPacket_ShouldParseCorrectly()
    {
        // Arrange
        var msgId = 1;
        var msgSeq = (ushort)100;

        // Create a proper protobuf message
        var testMessage = new EchoReq { Message = "test payload" };
        var protoBytes = testMessage.ToByteArray();

        // Build wire format: [EndPointHeader(4)][GameHeader(8)][Payload...]
        var totalSize = EndPointHeader.SizeOf + GameHeader.SizeOf + protoBytes.Length;
        var buffer = new byte[totalSize];

        var offset = 0;
        var endPointHeader = new EndPointHeader { TotalLength = totalSize };
        MemoryMarshal.Write(buffer.AsSpan(offset), in endPointHeader);
        offset += EndPointHeader.SizeOf;

        var gameHeader = new GameHeader { MsgId = msgId, SequenceId = msgSeq };
        MemoryMarshal.Write(buffer.AsSpan(offset), in gameHeader);
        offset += GameHeader.SizeOf;

        protoBytes.CopyTo(buffer, offset);

        // Act
        var (header, message) = PacketHelper.ParseClientPacket(buffer, EchoReq.Parser);

        // Assert
        header.MsgId.Should().Be(msgId);
        header.SequenceId.Should().Be(msgSeq);
        ((EchoReq)message).Message.Should().Be("test payload");
    }

    [Fact]
    public void ParseClientPacket_PacketTooSmall_ShouldThrowException()
    {
        // Arrange
        var buffer = new byte[EndPointHeader.SizeOf + GameHeader.SizeOf - 1];

        // Act
        Action act = () => PacketHelper.ParseClientPacket(buffer, EchoReq.Parser);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Payload too small*");
    }
}
