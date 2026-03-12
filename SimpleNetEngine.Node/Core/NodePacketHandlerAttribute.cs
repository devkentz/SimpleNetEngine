using System;

namespace SimpleNetEngine.Node.Core;

/// <summary>
/// Node Packet Handler Attribute (Node Service Mesh - Control Plane).
/// 서버 간 RPC 패킷 처리 메서드에 적용됩니다.
///
/// 사용 예:
/// [NodeController]
/// public class ActorSyncController
/// {
///     [NodePacketHandler(InternalOpCode.ActorSync)]
///     public Task&lt;ActorSyncResponse&gt; Sync(ActorSyncRequest request) { ... }
/// }
///
/// Stateful 핸들러 (Actor 필요 시):
///     [NodePacketHandler(InternalOpCode.ActorCommand)]
///     public Task&lt;CommandResponse&gt; Command(INodeActor actor, CommandRequest request) { ... }
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public class NodePacketHandlerAttribute : Attribute
{
    public long MsgId { get; }

    public NodePacketHandlerAttribute(long msgId)
    {
        MsgId = msgId;
    }
}
