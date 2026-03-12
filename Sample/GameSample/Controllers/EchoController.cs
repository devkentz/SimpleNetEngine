using Proto.Node.Sample;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;
using Proto.Sample;
using SimpleNetEngine.Node.Network;

namespace GameSample.Controllers;

/// <summary>
/// Echo 패킷 핸들러 컨트롤러
/// 클라이언트로부터 받은 메시지를 그대로 응답 (개발/테스트용)
/// </summary>
[UserController]
public class EchoController(ILogger<EchoController> logger, INodeSender nodeSender)
{
    [UserPacketHandler(EchoReq.MsgId)]
    public Task<Response> EchoHandler(ISessionActor actor, EchoReq req)
    {
        logger.LogInformation(
            "Echo request: ActorId={ActorId}, UserId={UserId}, Message={Message}",
            actor.ActorId, actor.UserId, req.Message);

        return Task.FromResult(Response.Ok(new EchoRes
        {
            Message = $"Echo: {req.Message}",
            Timestamp = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }).UseEncrypt());
    }
    
    [UserPacketHandler(NodeEchoReq.MsgId)]
    public async Task<Response> NodeEchoHandler(ISessionActor actor, NodeEchoReq req)
    {
        var res = await nodeSender.RequestApiAsync<ServiceMeshEchoReq, ServiceMeshEchoRes>("EchoNode", 
            new ServiceMeshEchoReq
        {
            Message = req.Message,
        });
        
        return Response.Ok(new NodeEchoRes
        {
            Message = $"NodeId:{res.NodeId} Echo: {res.Message}",
            Timestamp = (int)res.Timestamp
        });
    }
}
