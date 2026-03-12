using Microsoft.Extensions.Options;
using Proto.Node.Sample;
using SimpleNetEngine.Node.Config;
using SimpleNetEngine.Node.Core;

namespace NodeSample.Controllers;

[NodeController]
public class EchoController(ILogger<EchoController> logger, IOptions<NodeConfig> nodeConfig)
{
    [NodePacketHandler(ServiceMeshEchoReq.MsgId)]
    public Task<ServiceMeshEchoRes> HandleEcho(ServiceMeshEchoReq req)
    {
        logger.LogInformation("ServiceMesh Echo received: {Message}", req.Message);

        var res = new ServiceMeshEchoRes
        {
            Message = req.Message,
            NodeId = nodeConfig.Value.NodeId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        return Task.FromResult(res);
    }
}
