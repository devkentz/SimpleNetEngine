using System;

namespace SimpleNetEngine.Node.Core;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class NodeControllerAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public class PacketHandlerAttribute : Attribute
{
    public long MsgId { get; }
    public PacketHandlerAttribute(long msgId)
    {
        MsgId = msgId;
    }
}