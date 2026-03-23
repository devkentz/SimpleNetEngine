using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;

namespace SimpleNetEngine.Game.Network;

/// <summary>
/// GameServer → Client Notification 전송 추상화
/// Actor 외부 (NodeController, background service 등)에서 사용.
/// 요청 파이프라인 내부에서는 PacketContext.SendNtf를 사용할 것.
/// </summary>
public interface IClientSender
{
    /// <summary>
    /// 클라이언트에 Notification 전송 (requestId=0, sequenceId 자동 증가)
    /// GSC Direct P2P 경유: GameServer → Gateway → Client
    /// </summary>
    void SendNtf(ISessionActor actor, Response response);
}

/// <summary>
/// IClientSender 기본 구현
/// GameSessionChannelListener.SendResponse를 래핑
/// </summary>
internal sealed class ClientSender(GameSessionChannelListener gscListener) : IClientSender
{
    public void SendNtf(ISessionActor actor, Response response)
    {
        gscListener.SendResponse(
            actor.GatewayNodeId,
            actor.ActorId,
            response,
            requestId: 0,
            sequenceId: actor.NextSequenceId());
    }
}
