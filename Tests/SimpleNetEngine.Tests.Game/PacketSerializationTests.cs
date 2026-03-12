using FluentAssertions;
using SimpleNetEngine.Protocol.Packets;
using System.Runtime.InteropServices;
using Proto.Test; // For EchoReq
using NetMQ;
using Xunit;

namespace SimpleNetEngine.Tests.Game;

public class PacketSerializationTests
{
    [Fact]
    public void NodePacket_Serialization_And_Deserialization_Should_Work()
    {
        // Arrange
        var message = new EchoReq { Message = "hello world" };

        // Act
        using var packet = NodePacket.Create(actorId: 333, srcId: 20, destId: 10, requestKey: 77, isReply: true, message: message);

        // Serialize process simulating zero-copy Msg transfer
        var sentMsg = new Msg();
        sentMsg.InitPool(packet.Msg.Size);
        packet.Msg.Slice().CopyTo(sentMsg.Slice()); // manually copy memory buffer

        using var deserialized = NodePacket.Create(ref sentMsg);

        // Assert
        deserialized.Header.Dest.Should().Be(10);
        deserialized.Header.Source.Should().Be(20);
        deserialized.Header.ActorId.Should().Be(333);
        deserialized.Header.IsReply.Should().Be(1);
        deserialized.Header.RequestKey.Should().Be(77);

        // Validate payload deserializes correctly
        var readMessage = EchoReq.Parser.ParseFrom(deserialized.Payload.ToArray());
        readMessage.Message.Should().Be("hello world");
    }
}
