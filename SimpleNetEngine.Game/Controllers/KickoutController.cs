using Game.Protocol;
using Internal.Protocol;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Network;
using SimpleNetEngine.Game.Session;
using SimpleNetEngine.Game.Services;
using SimpleNetEngine.Node.Core;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Game.Controllers;

/// <summary>
/// 라이브러리 내장 Kickout 수신 컨트롤러
/// Cross-Node 중복 로그인 시 기존 노드에서 Actor를 정리
///
/// 플로우:
/// 1. 새 노드의 LoginController → KickoutMessageHandler.SendKickoutRequestAsync
/// 2. → 이 노드의 KickoutController.HandleKickout (이 메서드)
/// 3. → ILoginHandler.OnKickoutAsync (앱 Hook: 데이터 저장 + DisconnectAction 결정)
/// 4-A. TerminateSession: Actor 즉시 제거 (재접속 불가)
/// 4-B. AllowSessionResume: Disconnected 전이 + Grace Period (재접속 허용)
/// 5. → Gateway에 DisconnectClient RPC
///
/// Redis 세션 삭제: 조건부 삭제 (DeleteSessionIfMatchAsync)로 내 SessionId와 일치할 때만 삭제.
/// 새 노드가 이미 덮어쓴 경우 자동으로 보호됨.
/// </summary>
[NodeController]
public class KickoutController(
    ILogger<KickoutController> logger,
    ISessionActorManager actorManager,
    ActorDisconnectHandler disconnectHandler,
    GatewayDisconnectQueue disconnectQueue,
    IClientSender clientSender,
    ILoginHandler loginHandler)
{
    [NodePacketHandler(ServiceMeshKickoutReq.MsgId)]
    public async Task<ServiceMeshKickoutRes> HandleKickout(ServiceMeshKickoutReq req)
    {
        logger.LogDebug(
            "Kickout request received: UserId={UserId}, SessionId={SessionId}",
            req.UserId, req.SessionId);

        // 1. Actor 조회
        var actorResult = actorManager.GetActor(req.SessionId);
        if (actorResult.IsFailure)
        {
            logger.LogWarning(
                "Kickout: Actor not found for SessionId={SessionId}, UserId={UserId}",
                req.SessionId, req.UserId);

            return new ServiceMeshKickoutRes
            {
                UserId = req.UserId,
                Success = false,
                ErrorCode = ServiceMeshKickoutErrorCode.UserNotFound
            };
        }

        var actor = actorResult.Value;
        var sessionId = req.SessionId;

        // 2. 클라이언트에 KickoutNtf 전송 (Actor 제거/Disconnect 전)
        clientSender.SendNtf(actor, Response.Ntf(new KickoutNtf { Reason = EKickoutReason.DuplicateLogin }));

        // 3. 앱 Hook + DisconnectAction 분기를 단일 mailbox 턴에서 처리
        await actor.ExecuteAsync(async _ =>
        {
            var action = await loginHandler.OnKickoutAsync(actor, EKickoutReason.DuplicateLogin);

            if (action == DisconnectAction.AllowSessionResume)
            {
                await disconnectHandler.AllowSessionResumeAsync(actor, loginHandler);
            }
            else
            {
                await disconnectHandler.TerminateSessionAsync(actor, loginHandler);
            }
        });

        // 4. Gateway 소켓 해제를 대기열에 예약
        disconnectQueue.Schedule(sessionId, req.GatewayNodeId);

        logger.LogDebug(
            "Kickout completed: UserId={UserId}, SessionId={SessionId}",
            req.UserId, sessionId);

        return new ServiceMeshKickoutRes
        {
            UserId = req.UserId,
            Success = true,
            ErrorCode = ServiceMeshKickoutErrorCode.Success
        };
    }

}
