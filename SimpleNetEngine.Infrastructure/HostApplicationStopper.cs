using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SimpleNetEngine.Infrastructure;

/// <summary>
/// IHostApplicationLifetime을 사용하여 실제 애플리케이션 종료를 수행하는 구현체입니다.
/// </summary>
public class HostApplicationStopper(IHostApplicationLifetime appLifetime, ILogger<HostApplicationStopper> logger)
    : IApplicationStopper
{
    public void StopApplication()
    {
        logger.LogWarning("Graceful shutdown initiated by ApplicationStopper");
        appLifetime.StopApplication();
    }
}
