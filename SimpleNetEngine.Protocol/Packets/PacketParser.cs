using SimpleNetEngine.Protocol.Memory;

namespace SimpleNetEngine.Protocol.Packets;

public interface IPacketParser<T>
{
    public IReadOnlyList<T> Parse(ArrayPoolBufferWriter buffer);
}
