using SimpleNetEngine.Node.Core;

namespace NodeSample.Services;

public class NodeSampleHostedService(
    NodeService nodeService,
    ILogger<NodeSampleHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("=== Starting NodeSample (Stateless Service) ===");

        await nodeService.StartAsync();
        logger.LogInformation("Service Mesh started via NodeService");

        logger.LogInformation("=== NodeSample Started Successfully ===");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping NodeSample...");
        nodeService.Stop();
        logger.LogInformation("NodeSample stopped");
        return Task.CompletedTask;
    }
}
