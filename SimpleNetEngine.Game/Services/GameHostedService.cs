using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using SimpleNetEngine.Game.Options;
using SimpleNetEngine.Game.Network;
using SimpleNetEngine.Node.Core;
using SimpleNetEngine.Node.Network;
using SimpleNetEngine.Node.Config;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Game.Services;

/// <summary>
/// GameServer 생명주기 관리 HostedService
/// </summary>
public class GameHostedService : IHostedService
{
    private readonly ILogger<GameHostedService> _logger;
    private readonly IOptions<GameOptions> _options;
    private readonly IOptions<NodeConfig> _nodeConfig;
    private readonly GameSessionChannelListener _gscListener;
    private readonly NodeService _nodeService;
    private readonly KickoutMessageHandler _kickoutHandler;

    public GameHostedService(
        ILogger<GameHostedService> logger,
        IOptions<GameOptions> options,
        IOptions<NodeConfig> nodeConfig,
        GameSessionChannelListener gscListener,
        NodeService nodeService,
        KickoutMessageHandler kickoutHandler)
    {
        _logger = logger;
        _options = options;
        _nodeConfig = nodeConfig;
        _gscListener = gscListener;
        _nodeService = nodeService;
        _kickoutHandler = kickoutHandler;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== Starting GameServer ===");

        // 1. GameSessionChannel Listener 먼저 시작 (동적 포트 할당 시 실제 포트를 알아야 하므로)
        await _gscListener.StartAsync();
        _logger.LogInformation("GameSessionChannel RouterSocket bound on port {Port}", _gscListener.BoundPort);

        // 2. 실제 바인딩된 포트로 NodeConfig 메타데이터 업데이트 (Gateway가 이 포트로 Connect)
        _nodeConfig.Value.Metadata[NodeMetadataKeys.GameSessionChannelPort] = _gscListener.BoundPort.ToString();

        // 3. Service Mesh 시작 (클러스터 등록 및 Heartbeat - 올바른 포트 메타데이터 포함)
        await _nodeService.StartAsync();
        _logger.LogInformation("Service Mesh started via NodeService");

        _logger.LogInformation("=== GameServer Started Successfully ===");
        _logger.LogInformation("GameServer NodeId: {NodeId}", _options.Value.GameNodeId);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping GameServer...");
        _gscListener.Dispose();
        _kickoutHandler.Dispose();
        _nodeService.Stop();
        _logger.LogInformation("GameServer stopped");
        return Task.CompletedTask;
    }
}
