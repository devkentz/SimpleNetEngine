using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Node.Core;

namespace SimpleNetEngine.Node.Extensions;

/// <summary>
/// NodeController 등록 확장 메서드
/// DI에 등록된 INodeHandlerRegistrar를 auto-discover하여 NodeDispatcher에 핸들러 등록
/// </summary>
public static class NodeControllerExtensions
{
    /// <summary>
    /// NodeDispatcher를 DI에 등록하고, 모든 INodeHandlerRegistrar로부터 핸들러를 자동 수집한다.
    /// Source Generator가 생성한 AddGeneratedNodeControllers()를 먼저 호출하여
    /// INodeHandlerRegistrar를 DI에 등록한 후 이 메서드를 호출해야 한다.
    /// </summary>
    public static IServiceCollection AddNodeControllers(this IServiceCollection services)
    {
        services.AddSingleton<INodeDispatcher>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var dispatcher = new NodeDispatcher(loggerFactory.CreateLogger<NodeDispatcher>());

            foreach (var registrar in sp.GetServices<INodeHandlerRegistrar>())
            {
                registrar.RegisterHandlers(dispatcher);
            }

            loggerFactory.CreateLogger("NodeControllerExtensions")
                .LogInformation("NodeController registration completed (Auto-discovered, Zero-Reflection)");

            return dispatcher;
        });

        return services;
    }
}
