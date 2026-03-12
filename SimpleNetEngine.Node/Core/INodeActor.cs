using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Node.Core;

public interface INodeActor
{
    long ActorId { get; }
    void Push(NodePacket packet);
}
