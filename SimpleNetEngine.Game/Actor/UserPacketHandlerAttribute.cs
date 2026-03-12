using System;

namespace SimpleNetEngine.Game.Actor;

/// <summary>
/// User Packet Handler Attribute (Game Session Channel - Data Plane).
/// 클라이언트 패킷 처리 메서드에 적용됩니다.
///
/// 사용 예:
/// [UserController]
/// public class EchoController
/// {
///     [UserPacketHandler(ClientOpCode.Echo)]
///     public Task&lt;Response&gt; Echo(IActor actor, EchoRequest request) { ... }
/// }
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public class UserPacketHandlerAttribute : Attribute
{
    public int MsgId { get; }

    public UserPacketHandlerAttribute(int msgId)
    {
        MsgId = msgId;
    }
}
