using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleNetEngine.Gateway.Options;
using SimpleNetEngine.Gateway.Network;
using SimpleNetEngine.Node.Config;
using SimpleNetEngine.Node.Core;

namespace SimpleNetEngine.Gateway.Services;

/// <summary>
/// Gateway 생명주기 관리 HostedService
/// </summary>
public class GatewayHostedService : IHostedService
{
    private readonly GatewayTcpServer _tcpServer;
    private readonly GamePacketRouter _packetRouter;
    private readonly NodeService _nodeService;
    private readonly IOptions<GatewayOptions> _options;
    private readonly IOptions<NodeConfig> _nodeConfig;
    private readonly ILogger<GatewayHostedService> _logger;

    public GatewayHostedService(
        GatewayTcpServer tcpServer,
        GamePacketRouter packetRouter,
        NodeService nodeService,
        IOptions<GatewayOptions> options,
        IOptions<NodeConfig> nodeConfig,
        ILogger<GatewayHostedService> logger)
    {
        _tcpServer = tcpServer;
        _packetRouter = packetRouter;
        _nodeService = nodeService;
        _options = options;
        _nodeConfig = nodeConfig;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var portMode = _options.Value.AllowDynamicPort ? "dynamic" : "fixed";
        _logger.LogInformation("Starting Gateway on port {Port} (mode: {Mode})", _options.Value.TcpPort, portMode);

        // TCP 서버 시작
        _tcpServer.Start();
        _packetRouter.StartAsync();

        // 실제 바인딩된 클라이언트 TCP 포트를 NodeConfig 메타데이터에 저장 (Redis 서버 레지스트리에 포함됨)
        _nodeConfig.Value.Metadata[NodeMetadataKeys.GatewayClientTcpPort] = _tcpServer.Port.ToString();

        // Service Mesh 시작 (클러스터 등록 및 Heartbeat — 올바른 포트 메타데이터 포함)
        await _nodeService.StartAsync();
        _logger.LogInformation("Service Mesh started via NodeService");

        _logger.LogInformation("Gateway started successfully on port {Port}", _tcpServer.Port);
        _logger.LogInformation("P2P connections will be established automatically via Node Mesh events");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Gateway...");
        _nodeService.Stop();
        _tcpServer.Stop();
        _packetRouter.Dispose();
        return Task.CompletedTask;
    }
}
