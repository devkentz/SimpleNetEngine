using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SimpleNetEngine.Node.Core;

/// <summary>
/// Stateless Service용 범용 HostedService.
/// NodeService.StartAsync/Stop을 래핑하여 앱에서 별도 HostedService를 만들 필요 없음.
/// </summary>
public sealed class StatelessHostedService(
    NodeService nodeService,
    ILogger<StatelessHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting Stateless Service (Service Mesh)...");
        await nodeService.StartAsync();
        logger.LogInformation("Stateless Service started");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping Stateless Service...");
        nodeService.Stop();
        logger.LogInformation("Stateless Service stopped");
        return Task.CompletedTask;
    }
}
