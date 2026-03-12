using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using SimpleNetEngine.Gateway.Options;
using SimpleNetEngine.Gateway.Services;
using SimpleNetEngine.Gateway.Core;
using SimpleNetEngine.Gateway.Network;
using SimpleNetEngine.Node.Extensions;
using SimpleNetEngine.Node.Config;
using SimpleNetEngine.Node.Core;
using SimpleNetEngine.ProtoGenerator;

namespace SimpleNetEngine.Gateway.Extensions;

/// <summary>
/// Gateway 빌더. AddGateway()에서 사용.
/// </summary>
public sealed class GatewayBuilder
{
    internal Action<GatewayOptions>? ConfigureOptionsAction { get; private set; }
    internal Type EventHandlerType { get; private set; } = typeof(GatewayNodeEventHandler);

    /// <summary>
    /// Gateway 옵션을 설정한다.
    /// NodeGuid, ServiceMeshPort를 GatewayOptions에 직접 설정 가능.
    /// </summary>
    public GatewayBuilder Configure(Action<GatewayOptions> configure)
    {
        ConfigureOptionsAction = configure;
        return this;
    }

    /// <summary>
    /// 커스텀 NodeEventHandler를 사용한다 (기본: GatewayNodeEventHandler).
    /// </summary>
    public GatewayBuilder UseEventHandler<T>() where T : GatewayNodeEventHandler
    {
        EventHandlerType = typeof(T);
        return this;
    }
}

public static class GatewayServiceExtensions
{
    /// <summary>
    /// Gateway 서비스를 DI 컨테이너에 등록
    /// </summary>
    public static IServiceCollection AddGatewayServices(
        this IServiceCollection services,
        Action<GatewayOptions> configureOptions)
    {
        // Options 패턴 사용
        services.Configure(configureOptions);

        // Redis 연결
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GatewayOptions>>().Value;
            return ConnectionMultiplexer.Connect(options.RedisConnectionString);
        });

        // Gateway 핵심 서비스
        services.AddSingleton<SessionMapper>();
        services.AddSingleton<GamePacketRouter>();
        services.AddSingleton<GatewayTcpServer>();

        // Gateway 클라이언트 세션 레지스트리
        services.AddSingleton<GatewaySessionRegistry>();

        // HostedService 등록
        services.AddHostedService<GatewayHostedService>();

        return services;
    }

    /// <summary>
    /// Gateway를 빌더 패턴으로 등록한다.
    /// 내부에서 AddGatewayServices + UseNode + AddNodeControllers를 자동 호출.
    ///
    /// 앱 레벨 NodeController가 있으면 AddGeneratedNodeControllers()를 별도 호출해야 함.
    /// (DI factory가 lazy이므로 호출 순서는 무관)
    /// </summary>
    public static IServiceCollection AddGateway(
        this IServiceCollection services,
        Action<GatewayBuilder> configure)
    {
        var builder = new GatewayBuilder();
        configure(builder);

        var configureGateway = builder.ConfigureOptionsAction
            ?? throw new InvalidOperationException("GatewayBuilder.Configure() must be called.");

        var gatewayOptions = new GatewayOptions();
        configureGateway(gatewayOptions);

        var nodeConfig = gatewayOptions.ToNodeConfig();
        gatewayOptions.GatewayNodeId = nodeConfig.NodeId;

        // Gateway 서비스 등록 (원본 람다 재호출로 모든 옵션 보존)
        services.AddGatewayServices(o =>
        {
            configureGateway(o);
            o.GatewayNodeId = gatewayOptions.GatewayNodeId;
        });

        // Node Service Mesh
        services.UseNode(Microsoft.Extensions.Options.Options.Create(nodeConfig));

        // NodeController 등록 (INodeHandlerRegistrar auto-discover)
        services.AddNodeControllers();

        // NodeEventHandler
        services.AddSingleton(typeof(NodeEventHandler), builder.EventHandlerType);

        return services;
    }
}
