using Google.Protobuf;
using Internal.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Node.Core;
using SimpleNetEngine.Node.Network;
using SimpleNetEngine.Gateway.Network;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Gateway.Core;

/// <summary>
/// Gateway의 Node 간 RPC 패킷 처리기.
/// INodeDispatcher를 사용하여 [NodeController] + [NodePacketHandler] 기반 핸들러로 라우팅합니다.
/// Sequential 모드로 동작하여 모든 패킷을 단일 스레드에서 순차 처리합니다.
/// </summary>
public class GatewayNodeEventHandler : SequentialNodeEventHandler
{
    private readonly INodeResponser _responser;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INodeDispatcher _dispatcher;
    private readonly GamePacketRouter _packetRouter;

    public GatewayNodeEventHandler(
        ILogger<GatewayNodeEventHandler> logger,
        INodeDispatcher dispatcher,
        INodeResponser responser,
        IServiceScopeFactory scopeFactory,
        GamePacketRouter packetRouter)
        : base(logger)
    {
        _dispatcher = dispatcher;
        _responser = responser;
        _scopeFactory = scopeFactory;
        _packetRouter = packetRouter;
    }

    /// <summary>
    /// NodePacket을 INodeDispatcher로 직접 라우팅
    /// QueuedResponseWriter에 의해 단일 스레드에서 순차적으로 호출됩니다.
    /// </summary>
    protected override async Task ProcessPacketInternalAsync(NodePacket packet)
    {
        IMessage? response = null;
        using (packet)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                response = await _dispatcher.DispatchAsync(scope.ServiceProvider, packet);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error processing node packet in GatewayNodeEventHandler");
            }

            if (response != null)
                _responser.Response(packet.Header, response);
        }
    }

    public override void OnJoinNode(RemoteNode remoteNode)
    {
        base.OnJoinNode(remoteNode);

        if (remoteNode.ServerType == EServerType.Game)
        {
            var metadata = remoteNode.ServerInfo.Metadata;

            if (metadata.TryGetValue(NodeMetadataKeys.GameSessionChannelPort, out var recvPortStr) &&
                int.TryParse(recvPortStr, out var recvPort) &&
                metadata.TryGetValue(NodeMetadataKeys.GameSessionChannelSendPort, out var sendPortStr) &&
                int.TryParse(sendPortStr, out var sendPort))
            {
                var ip = remoteNode.Address.Replace("tcp://", "").Split(':')[0];
                var recvEndpoint = $"tcp://{ip}:{recvPort}";
                var sendEndpoint = $"tcp://{ip}:{sendPort}";

                _logger.LogDebug(
                    "GameServer node {NodeId} joined. Connecting DataPlane: recv={RecvEp}, send={SendEp}",
                    remoteNode.Identity, recvEndpoint, sendEndpoint);
                _packetRouter.ConnectToGameServer(remoteNode.Identity, recvEndpoint, sendEndpoint);
            }
            else
            {
                _logger.LogWarning("GameServer node {NodeId} joined but GameSessionChannel ports not found in Metadata.", remoteNode.Identity);
            }
        }
    }

    public override void OnLeaveNode(RemoteNode remoteNode)
    {
        base.OnLeaveNode(remoteNode);

        if (remoteNode.ServerType == EServerType.Game)
        {
            _logger.LogDebug("GameServer node {NodeId} left Service Mesh. Disconnecting from Data Plane.", remoteNode.Identity);
            _packetRouter.DisconnectFromGameServer(remoteNode.Identity);
        }
    }
}
