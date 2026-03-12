using Game.Protocol;
using Internal.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Options;
using SimpleNetEngine.Game.Session;
using SimpleNetEngine.Node.Network;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Game.Controllers;

/// <summary>
/// 라이브러리 내장 ReconnectReq 핸들러
/// Authenticating 상태의 임시 Actor에서 실행됨
///
/// 플로우:
/// 1. ReconnectKey로 Redis 역인덱스 조회 → userId
/// 2. userId로 SessionInfo 조회
/// 3. Same-Node: 로컬 Actor 복원 (Disconnected → Active)
/// 4. Cross-Node: Gateway Re-route → 클라이언트가 ReconnectReq 재전송 → 기존 노드에서 Same-Node 처리
/// </summary>
public class ReconnectController(
    ILogger<ReconnectController> logger,
    ISessionStore sessionStore,
    ISessionActorManager actorManager,
    ActorDisposeQueue disposeQueue,
    INodeSender nodeSender,
    ILoginHandler loginHandler,
    IOptions<GameOptions> options,
    TimeProvider timeProvider)
{
    private readonly GameOptions _options = options.Value;

    public async Task<Response> HandleReconnect(ISessionActor tempActor, ReconnectReq req)
    {
        if (!Guid.TryParse(req.ReconnectKey, out var reconnectKey))
        {
            logger.LogWarning("Invalid ReconnectKey format: ActorId={ActorId}", tempActor.ActorId);
            return Response.Ok(new ReconnectRes
            {
                Success = false,
                ErrorMessage = "Invalid reconnect key format"
            });
        }

        // 1. ReconnectKey → UserId (Redis 역인덱스)
        var userId = await sessionStore.GetUserIdByReconnectKeyAsync(reconnectKey);
        if (userId == null)
        {
            logger.LogWarning(
                "ReconnectKey not found in Redis: ActorId={ActorId}",
                tempActor.ActorId);
            return Response.Ok(new ReconnectRes
            {
                Success = false,
                ErrorMessage = "Reconnect key expired or invalid"
            });
        }

        // 2. UserId → SessionInfo (Redis)
        var session = await sessionStore.GetSessionAsync(userId.Value);
        if (session == null)
        {
            logger.LogWarning(
                "Session not found for UserId={UserId}: ActorId={ActorId}",
                userId.Value, tempActor.ActorId);
            return Response.Ok(new ReconnectRes
            {
                Success = false,
                ErrorMessage = "Session expired"
            });
        }

        logger.LogInformation(
            "ReconnectReq: UserId={UserId}, SessionNode={SessionNode}, CurrentNode={CurrentNode}",
            userId.Value, session.GameServerNodeId, _options.GameNodeId);

        // 3. Same-Node vs Cross-Node 분기
        if (session.GameServerNodeId == _options.GameNodeId)
        {
            return await HandleSameNodeReconnect(tempActor, userId.Value, session, reconnectKey);
        }

        return await HandleCrossNodeReconnect(tempActor, session);
    }

    /// <summary>
    /// Same-Node 재접속: 로컬 Disconnected Actor 복원
    /// </summary>
    private async Task<Response> HandleSameNodeReconnect(
        ISessionActor tempActor, long userId, SessionInfo session, Guid oldReconnectKey)
    {
        // 기존 Actor 조회 (Disconnected 상태여야 함)
        var existingResult = actorManager.GetActor(session.SessionId);
        if (existingResult.IsFailure)
        {
            logger.LogWarning(
                "Existing Actor not found for reconnect: SessionId={SessionId}, UserId={UserId}",
                session.SessionId, userId);
            return Response.Ok(new ReconnectRes
            {
                Success = false,
                ErrorMessage = "Session actor not found"
            });
        }

        var existingActor = existingResult.Value;
        if (existingActor.Status != ActorState.Disconnected)
        {
            logger.LogWarning(
                "Actor not in Disconnected state: SessionId={SessionId}, Status={Status}",
                session.SessionId, existingActor.Status);
            return Response.Ok(new ReconnectRes
            {
                Success = false,
                ErrorMessage = "Session is not in disconnected state"
            });
        }

        // Disconnected 타임스탬프 초기화
        existingActor.ClearDisconnected();

        // 라우팅 정보 갱신 (새 Gateway/TCP 연결)
        existingActor.UpdateRouting(tempActor.GatewayNodeId);

        // old ReconnectKey 삭제 + 새 키 발급
        await sessionStore.DeleteReconnectKeyAsync(oldReconnectKey);
        var newKey = existingActor.RegenerateReconnectKey();
        await sessionStore.SetReconnectKeyAsync(newKey, userId);

        // Redis 세션 정보 갱신 (새 GatewayNodeId + 새 SessionId)
        var updatedSession = session with
        {
            SessionId = tempActor.ActorId,
            GatewayNodeId = tempActor.GatewayNodeId,
            LastActivityUtc = timeProvider.GetUtcNow()
        };
        await sessionStore.SetSessionAsync(userId, updatedSession);

        // Active 상태 복원
        existingActor.Status = ActorState.Active;

        // 앱 Hook: 재접속 성공
        await loginHandler.OnReconnectedAsync(existingActor);

        // Actor SessionId 스왑: 기존 Actor의 SessionId를 tempActor(Gateway가 알고 있는)의 것으로 교체
        // Gateway RPC 불필요 — Gateway는 이미 tempActor.ActorId로 패킷을 보내고 있음
        // 부수 효과: 기존 소켓의 늦은 ClientDisconnected(oldSessionId)가 ActorManager에서 매칭 안 됨 → Race Condition 방지
        var tempSessionId = tempActor.ActorId;
        var removeResult = actorManager.UnregisterActor(tempSessionId);
        if (removeResult.IsSuccess)
            disposeQueue.Enqueue(removeResult.Value);

        var rekeyResult = actorManager.RekeyActor(session.SessionId, tempSessionId);
        if (rekeyResult.IsFailure)
        {
            logger.LogError(
                "Failed to rekey Actor: UserId={UserId}, OldSessionId={OldSessionId}, NewSessionId={NewSessionId}, Error={Error}",
                userId, session.SessionId, tempSessionId, rekeyResult.ErrorMessage);
        }

        logger.LogInformation(
            "Same-node reconnect completed: UserId={UserId}, SessionId={SessionId}, OldTempSessionId={TempSessionId}",
            userId, session.SessionId, tempActor.ActorId);

        return Response.Ok(new ReconnectRes
        {
            Success = true,
            NewReconnectKey = newKey.ToString()
        });
    }

    /// <summary>
    /// Cross-Node 재접속: Gateway Re-route로 기존 노드에 전달
    ///
    /// 기존 노드(A)에 Actor 인메모리 상태가 있으므로 Kickout하지 않고,
    /// Gateway pin을 A로 변경한 뒤 클라이언트가 ReconnectReq를 재전송하도록 유도.
    /// A에서 Same-Node Reconnect로 Actor를 복원한다.
    ///
    /// 순서 (race condition 방지):
    /// 1. Node A에 임시 Actor 사전 생성 (is_reroute=true → Authenticating 상태)
    /// 2. Gateway에 Re-route 지시 (pin 변경)
    /// 3. 로컬 임시 Actor 제거
    /// 4. 클라이언트에 재시도 응답
    ///
    /// ReconnectKey는 삭제하지 않음 — A가 처리할 때 사용해야 함.
    /// </summary>
    private async Task<Response> HandleCrossNodeReconnect(
        ISessionActor tempActor, SessionInfo session)
    {
        logger.LogInformation(
            "Cross-node reconnect: re-routing to owning node. TempActorId={TempActorId}, OwnerNode={OwnerNode}",
            tempActor.ActorId, session.GameServerNodeId);

        // 1. Node A에 임시 Actor 사전 생성 (Reroute 전에 반드시 완료)
        //    is_reroute=true: Authenticating 상태로 생성, ReadyToHandshakeNtf 스킵
        //    Gateway가 이미 SharedSecret을 보유하므로 ECDH 키 불필요
        try
        {
            var ntfRes = await nodeSender.RequestAsync<ServiceMeshNewUserNtfReq, ServiceMeshNewUserNtfRes>(
                NodePacket.ServerActorId,
                session.GameServerNodeId,
                new ServiceMeshNewUserNtfReq
                {
                    GatewayNodeId = tempActor.GatewayNodeId,
                    SessionId = tempActor.ActorId,
                    IsReroute = true
                });

            if (!ntfRes.Success)
            {
                logger.LogError(
                    "Failed to pre-create actor on target node: TempActorId={TempActorId}, TargetNode={TargetNode}",
                    tempActor.ActorId, session.GameServerNodeId);
                return Response.Ok(new ReconnectRes
                {
                    Success = false,
                    ErrorMessage = "Cross-node actor creation failed"
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "NtfNewUser RPC failed for cross-node reconnect: TempActorId={TempActorId}, TargetNode={TargetNode}",
                tempActor.ActorId, session.GameServerNodeId);
            
            return Response.Ok(new ReconnectRes
            {
                Success = false,
                ErrorMessage = "Cross-node RPC failed"
            });
        }

        // 2. Gateway에 Re-route 지시 (Node A에 Actor가 존재하는 것을 보장한 후)
        try
        {
            await nodeSender.RequestAsync<ServiceMeshRerouteSocketReq, ServiceMeshRerouteSocketRes>(
                NodePacket.ServerActorId,
                tempActor.GatewayNodeId,
                new ServiceMeshRerouteSocketReq
                {
                    GatewayNodeId = tempActor.GatewayNodeId,
                    SessionId = tempActor.ActorId,
                    TargetNodeId = session.GameServerNodeId
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to re-route session: TempActorId={TempActorId}, TargetNode={TargetNode}",
                tempActor.ActorId, session.GameServerNodeId);

            return Response.Ok(new ReconnectRes
            {
                Success = false,
                ErrorMessage = "Re-route failed"
            });
        }

        // 3. 임시 Actor 제거 (mailbox 내부에서 self-deadlock 방지)
        var unregResult = actorManager.UnregisterActor(tempActor.ActorId);
        if (unregResult.IsSuccess)
            disposeQueue.Enqueue(unregResult.Value);

        // 4. 클라이언트에 재시도 응답 (ReconnectKey는 유지 — A에서 사용)
        return Response.Ok(new ReconnectRes
        {
            Success = false,
            RequiresRetry = true,
            ErrorMessage = "SESSION_ON_OTHER_NODE"
        });
    }
}
