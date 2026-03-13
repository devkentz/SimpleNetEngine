using Internal.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Middleware;
using SimpleNetEngine.Game.Services;
using SimpleNetEngine.Game.Session;
using SimpleNetEngine.Node.Core;
using Game.Protocol;

namespace SimpleNetEngine.Game.Network;

/// <summary>
/// GameServer 측 접속 관련 Node RPC 핸들러
/// Gateway로부터 NtfNewUser를 수신하면:
/// 1. Gateway가 발급한 SessionId로 익명 Actor 생성
/// 2. ReadyToHandshakeNtf를 GSC 경유로 클라이언트에 전송 (SessionId 기반 라우팅)
///
/// ClientDisconnected 수신 시:
/// - Active Actor: Disconnected 상태 전이 + OnDisconnectedAsync Hook
/// - Created/Authenticating Actor: 즉시 제거 (유저 데이터 없음)
/// </summary>
[NodeController]
public class ConnectionController(
    ILogger<ConnectionController> logger,
    ISessionActorManager actorManager,
    GatewayDisconnectQueue disconnectQueue,
    ActorDisconnectHandler disconnectHandler,
    ILoginHandler loginHandler,
    ISessionActorFactory actorFactory,
    IMessageDispatcher messageDispatcher,
    IServiceScopeFactory scopeFactory,
    MiddlewarePipelineFactory pipelineFactory,
    GameSessionChannelListener gscListener)
{
    [NodePacketHandler(ServiceMeshNewUserNtfReq.MsgId)]
    public Task<ServiceMeshNewUserNtfRes> HandleNtfNewUser(ServiceMeshNewUserNtfReq req)
    {
        var gatewayNodeId = req.GatewayNodeId;
        var sessionId = req.SessionId;

        logger.LogInformation(
            "NtfNewUser received: Gateway={GatewayNodeId}, SessionId={SessionId}, IsReroute={IsReroute}. Creating anonymous Actor",
            gatewayNodeId, sessionId, req.IsReroute);

        // 1. 익명 Actor 생성 (Gateway가 발급한 SessionId 사용, SocketId 불필요)
        var actor = actorFactory.Create(
            actorId: sessionId,
            userId: 0,
            gatewayNodeId: gatewayNodeId,
            scopeFactory: scopeFactory,
            dispatcher: messageDispatcher,
            pipeline: pipelineFactory.CreateDefaultPipeline(),
            logger: logger);

        if (req.IsReroute)
        {
            // Cross-node reconnect: Authenticating 상태로 직접 생성 (Handshake 스킵)
            // Gateway가 이미 SharedSecret을 보유하므로 ECDH 키 불필요
            actor.Status = ActorState.Authenticating;
        }
        else
        {
            // Normal flow: Gateway ECDH 공개키 + 서명을 Actor 상태에 저장 (HandshakeController에서 사용)
            if (!req.GatewayEphemeralPublicKey.IsEmpty)
            {
                actor.State["GatewayEphemeralPublicKey"] = req.GatewayEphemeralPublicKey.ToByteArray();
                if (!req.GatewayEphemeralSignature.IsEmpty)
                    actor.State["GatewayEphemeralSignature"] = req.GatewayEphemeralSignature.ToByteArray();
            }
        }

        var addResult = actorManager.TryAddActor(actor);
        if (addResult.IsFailure)
        {
            logger.LogWarning(
                "Failed to add Actor: SessionId={SessionId}, Error={ErrorCode}. Disposing.",
                sessionId, addResult.Error);
            actor.Dispose();
            return Task.FromResult(new ServiceMeshNewUserNtfRes { Success = false });
        }

        // Reroute: ReadyToHandshakeNtf 전송하지 않음 (클라이언트가 이미 Handshake 완료)
        if (!req.IsReroute)
        {
            // 2. ReadyToHandshakeNtf를 GSC 경유로 클라이언트에 전송
            //    GameServer는 SocketId를 모르므로 SessionId 기반 라우팅 사용
            //    Gateway가 SessionId → GatewaySession 매핑으로 올바른 클라이언트에 전달
            gscListener.SendResponse(
                gatewayNodeId,
                sessionId,
                Response.Ok(new ReadyToHandshakeNtf()),
                requestId: 0,
                sequenceId: (ushort)actor.NextSequenceId());

            logger.LogInformation(
                "ReadyToHandshakeNtf sent via GSC: SessionId={SessionId}, Gateway={GatewayNodeId}",
                sessionId, gatewayNodeId);
        }

        return Task.FromResult(new ServiceMeshNewUserNtfRes { Success = true });
    }

    [NodePacketHandler(ServiceMeshClientDisconnectedNtfReq.MsgId)]
    public async Task<ServiceMeshClientDisconnectedNtfRes> HandleClientDisconnected(ServiceMeshClientDisconnectedNtfReq req)
    {
        var sessionId = req.SessionId;

        logger.LogInformation(
            "ClientDisconnected received: Gateway={GatewayNodeId}, SessionId={SessionId}",
            req.GatewayNodeId, sessionId);

        // 대기열에 예약된 disconnect 취소 (클라이언트가 자연스럽게 끊은 경우)
        disconnectQueue.Cancel(sessionId);

        var actorResult = actorManager.GetActor(sessionId);
        if (actorResult.IsFailure)
        {
            // Kickout/TerminateSession 등으로 이미 정리된 경우 — 정상적인 레이스 컨디션
            logger.LogDebug(
                "Actor already removed for disconnect (likely kicked out): SessionId={SessionId}",
                sessionId);
            return new ServiceMeshClientDisconnectedNtfRes { Success = true };
        }

        var actor = actorResult.Value;

        // Active 상태: Actor mailbox를 통해 Disconnected 전이 + Hook
        // NodeController(Service Mesh) 스레드에서 직접 변경하면 Actor mailbox 컨슈머와 race condition 발생
        if (actor.Status == ActorState.Active)
        {
            await actor.ExecuteAsync(async _ =>
            {
                if (actor.Status != ActorState.Active)
                    return;

                await disconnectHandler.AllowSessionResumeAsync(actor, loginHandler);
            });

            return new ServiceMeshClientDisconnectedNtfRes { Success = true };
        }

        // Created/Authenticating 상태: 즉시 제거 (유저 데이터 없음)
        var removeResult = actorManager.RemoveActor(sessionId);
        if (removeResult.IsFailure)
        {
            logger.LogWarning(
                "Failed to remove Actor: SessionId={SessionId}, Error={ErrorCode}",
                sessionId, removeResult.Error);
        }

        return new ServiceMeshClientDisconnectedNtfRes { Success = removeResult.IsSuccess };
    }
}
