using Internal.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Game.Network;
using SimpleNetEngine.Node.Network;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Game.Services;

/// <summary>
/// GatewayDisconnectQueue를 주기적으로 drain하여 Gateway에 disconnect RPC를 전송하는 BackgroundService.
/// Logout 시 응답이 클라이언트에 도달할 시간을 확보한 뒤 소켓을 끊는다.
/// </summary>
public class GatewayDisconnectService(
    GatewayDisconnectQueue disconnectQueue,
    INodeSender nodeSender,
    ILogger<GatewayDisconnectService> logger) : BackgroundService
{
    private static readonly TimeSpan DrainInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GatewayDisconnectService started: DrainInterval={Interval}s", DrainInterval.TotalSeconds);

        using var timer = new PeriodicTimer(DrainInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var items = disconnectQueue.DrainAll();
            foreach (var (sessionId, gatewayNodeId) in items)
            {
                try
                {
                    await nodeSender.RequestAsync<ServiceMeshDisconnectClientReq, ServiceMeshDisconnectClientRes>(
                        NodePacket.ServerActorId,
                        gatewayNodeId,
                        new ServiceMeshDisconnectClientReq
                        {
                            SessionId = sessionId,
                            GatewayNodeId = gatewayNodeId
                        });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Gateway disconnect failed: SessionId={SessionId}, Gateway={GatewayNodeId}",
                        sessionId, gatewayNodeId);
                }
            }

            if (items.Count > 0)
            {
                logger.LogDebug("GatewayDisconnectService: Disconnected {Count} session(s)", items.Count);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        var items = disconnectQueue.DrainAll();
        foreach (var (sessionId, gatewayNodeId) in items)
        {
            try
            {
                await nodeSender.RequestAsync<ServiceMeshDisconnectClientReq, ServiceMeshDisconnectClientRes>(
                    NodePacket.ServerActorId,
                    gatewayNodeId,
                    new ServiceMeshDisconnectClientReq
                    {
                        SessionId = sessionId,
                        GatewayNodeId = gatewayNodeId
                    });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Shutdown disconnect failed: SessionId={SessionId}, Gateway={GatewayNodeId}",
                    sessionId, gatewayNodeId);
            }
        }

        if (items.Count > 0)
            logger.LogDebug("GatewayDisconnectService shutdown: Disconnected {Count} remaining session(s)", items.Count);
    }
}
