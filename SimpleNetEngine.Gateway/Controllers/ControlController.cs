using SimpleNetEngine.Gateway.Core;
using Internal.Protocol;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Node.Core;

namespace SimpleNetEngine.Gateway.Controllers;

/// <summary>
/// Gateway 측 Control Plane RPC 핸들러
/// GameServer가 요청하는 세션 제어명령(Pin, Disconnect, Reroute)을 Service Mesh 통신으로 수행합니다.
/// </summary>
[NodeController]
public class ControlController(ILogger<ControlController> logger, GatewaySessionRegistry sessionRegistry)
{
    [NodePacketHandler(ServiceMeshDisconnectClientReq.MsgId)]
    public Task<ServiceMeshDisconnectClientRes> DisconnectClient(ServiceMeshDisconnectClientReq req)
    {
        if (sessionRegistry.TryGetBySessionId(req.SessionId, out var session))
        {
            session.Disconnect();
            logger.LogInformation("Client disconnected by GameServer RPC: SessionId={SessionId}", req.SessionId);
            return Task.FromResult(new ServiceMeshDisconnectClientRes { Success = true });
        }

        logger.LogWarning("Cannot disconnect via RPC: SessionId={SessionId} not found", req.SessionId);
        return Task.FromResult(new ServiceMeshDisconnectClientRes { Success = false });
    }

    [NodePacketHandler(ServiceMeshRerouteSocketReq.MsgId)]
    public Task<ServiceMeshRerouteSocketRes> RerouteSocket(ServiceMeshRerouteSocketReq req)
    {
        if (sessionRegistry.TryGetBySessionId(req.SessionId, out var rerouteSession))
        {
            rerouteSession.Reroute(req.TargetNodeId);
            logger.LogInformation("Session rerouted via RPC: SessionId={SessionId}, NewNodeId={NodeId}",
                req.SessionId, req.TargetNodeId);
            return Task.FromResult(new ServiceMeshRerouteSocketRes { Success = true });
        }

        logger.LogWarning("Cannot reroute via RPC: SessionId={SessionId} not found", req.SessionId);
        return Task.FromResult(new ServiceMeshRerouteSocketRes { Success = false });
    }

    [NodePacketHandler(ServiceMeshActivateEncryptionReq.MsgId)]
    public Task<ServiceMeshActivateEncryptionRes> ActivateEncryption(ServiceMeshActivateEncryptionReq req)
    {
        if (sessionRegistry.TryGetBySessionId(req.SessionId, out var session))
        {
            session.DeriveAndActivateEncryption(req.ClientEphemeralPublicKey.ToByteArray());
            logger.LogInformation("Encryption activated via RPC: SessionId={SessionId}", req.SessionId);
            return Task.FromResult(new ServiceMeshActivateEncryptionRes { Success = true });
        }

        logger.LogWarning("Cannot activate encryption via RPC: SessionId={SessionId} not found", req.SessionId);
        return Task.FromResult(new ServiceMeshActivateEncryptionRes { Success = false });
    }
}
