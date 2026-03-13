using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Middleware;
using SimpleNetEngine.Game.Network;
using SimpleNetEngine.Protocol.Packets;
using Microsoft.Extensions.Logging;
using Game.Protocol;

namespace SimpleNetEngine.Game.Core;

/// <summary>
/// GameServer Smart Hub (BFF - Backend For Frontend)
/// 모든 클라이언트 패킷의 허브 역할
///
/// Gateway가 OnConnected 시점에 SessionId를 즉시 발급하므로
/// 모든 패킷은 pinned 상태(sessionId > 0)로 도착.
/// HandlePacket은 Actor mailbox에 non-blocking push만 수행.
///
/// 비즈니스 로직은 Actor 내부에서 실행됨:
/// - HandshakeReq → HandshakeController (ActorState.Created)
/// - LoginGameReq → LoginController (ActorState.Authenticating)
/// - 게임 패킷 → UserController (ActorState.LoggedIn)
/// </summary>
public class GameServerHub(
    ILogger<GameServerHub> logger,
    ISessionActorManager actorManager) : IClientPacketHandler
{
    /// <summary>
    /// NetMQ Poller 스레드에서 호출됨 (블로킹 금지)
    /// 모든 패킷을 Actor mailbox에 동기 push.
    /// </summary>
    public void HandlePacket(PacketContext context)
    {
        var actorResult = actorManager.GetActor(context.SessionId);
        if (actorResult.IsFailure)
        {
            logger.LogWarning(
                "Actor not found: SessionId={SessionId}. Sending SESSION_EXPIRED.",
                context.SessionId);

            context.SendResponse?.Invoke(
                context.GatewayNodeId, context.SessionId,
                Response.Error((short)ErrorCode.GameSessionExpired),
                context.RequestId,
                0);
            context.Dispose();
            return;
        }

        actorResult.Value.Push(new PacketActorMessage(context));
    }
}
