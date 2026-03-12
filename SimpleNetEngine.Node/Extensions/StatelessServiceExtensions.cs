using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SimpleNetEngine.Node.Config;
using SimpleNetEngine.Node.Core;

namespace SimpleNetEngine.Node.Extensions;

/// <summary>
/// Stateless Service 통합 등록 확장 메서드.
/// UseNode + AddNodeControllers + StatelessEventController + StatelessHostedService를 한 번에 등록.
/// </summary>
public static class StatelessServiceExtensions
{
    /// <summary>
    /// Stateless Service를 등록한다 (기본 StatelessEventController 사용).
    ///
    /// 앱 레벨 NodeController가 있는 경우 AddGeneratedNodeControllers()를 별도 호출해야 함.
    /// (DI factory가 lazy이므로 호출 순서는 무관)
    /// </summary>
    public static IServiceCollection AddStatelessService(
        this IServiceCollection services,
        Action<StatelessServiceOptions> configureOptions)
        => services.AddStatelessService<StatelessEventController>(configureOptions);

    /// <summary>
    /// Stateless Service를 등록한다 (커스텀 EventHandler 사용).
    /// </summary>
    public static IServiceCollection AddStatelessService<TEventHandler>(
        this IServiceCollection services,
        Action<StatelessServiceOptions> configureOptions)
        where TEventHandler : ParallelNodeEventHandler
    {
        var options = new StatelessServiceOptions();
        configureOptions(options);

        var nodeConfig = options.ToNodeConfig();

        // Node Service Mesh 인프라 등록
        services.UseNode(Options.Create(nodeConfig));

        // NodeController + NodeDispatcher 등록 (INodeHandlerRegistrar auto-discover)
        services.AddNodeControllers();

        // Parallel 기반 Stateless EventHandler
        services.AddSingleton<NodeEventHandler, TEventHandler>();

        // 범용 HostedService (NodeService.StartAsync/Stop 래핑)
        services.AddHostedService<StatelessHostedService>();

        return services;
    }
}
