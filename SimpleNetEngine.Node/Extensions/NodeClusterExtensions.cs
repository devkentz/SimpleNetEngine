using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SimpleNetEngine.Protocol.Utils;
using SimpleNetEngine.Node.Cluster;
using SimpleNetEngine.Node.Config;
using SimpleNetEngine.Node.Core;
using SimpleNetEngine.Node.Network;
using SimpleNetEngine.Node.Utils;
using SimpleNetEngine.Node.MessageRedisValueParser;
using SimpleNetEngine.Infrastructure;
using SimpleNetEngine.Infrastructure.Middleware;
using SimpleNetEngine.Infrastructure.NetMQ;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Node.Extensions;

/// <summary>
/// Node 관련 확장 메서드
/// </summary> 
public static class NodeExtensions
{
    /// <summary>
    /// Node 서비스를 등록합니다.
    /// </summary>
    /// <param name="services">서비스 컬렉션</param>
    /// <param name="configuration">설정</param>
    /// <returns>서비스 컬렉션</returns>
    public static IServiceCollection UseNode(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NodeConfig>(configuration.GetSection("Node"));

        // Redis Value Parser 등록
        services.AddSingleton<IMessageRedisValueParser, JsonRedisValueParser>();

        // 새로운 Cluster Registry 추상화 등록
        services.AddSingleton<IClusterRegistry, RedisClusterRegistry>();

        // 애플리케이션 종료 처리기 등록
        services.AddSingleton<IApplicationStopper, HostApplicationStopper>();

        services.AddSingleton<INodeManager, NodeManager>();
        services.AddSingleton<INodeActorManager, NodeActorManager>();
        services.AddSingleton<NodeService>();
        services.AddSingleton<UniqueIdGenerator>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<NodeConfig>>().Value;
            return new UniqueIdGenerator(config.NodeGuid);
        });

        services.AddSingleton<NodePacketRouter>();
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IOptions<NodeConfig>>().Value;
            return new RequestCache<NodePacket>(config.RequestTimeoutMs);
        });
        services.AddSingleton<INodeSender, NodeSender>();
        services.AddSingleton<INodeResponser, NodeResponser>();
        services.AddSingleton<NodeCommunicator>();

        return services;
    }

    /// <summary>
    /// Node 서비스를 등록합니다 (IOptions 직접 전달).
    /// </summary>
    public static IServiceCollection UseNode(this IServiceCollection services, IOptions<NodeConfig> nodeConfigOptions)
    {
        NetMQ.BufferPool.SetCustomBufferPool(new NetMqArrayBufferPool());

        services.AddSingleton(nodeConfigOptions);

        // Redis Value Parser 등록
        services.AddSingleton<IMessageRedisValueParser, JsonRedisValueParser>();

        // Cluster Registry 추상화 등록
        services.AddSingleton<IClusterRegistry, RedisClusterRegistry>();

        // 애플리케이션 종료 처리기 등록
        services.AddSingleton<IApplicationStopper, HostApplicationStopper>();

        services.AddSingleton<INodeManager, NodeManager>();
        services.AddSingleton<INodeActorManager, NodeActorManager>();
        services.AddSingleton<NodeService>();
        services.AddSingleton<UniqueIdGenerator>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<NodeConfig>>().Value;
            return new UniqueIdGenerator(config.NodeGuid);
        });

        services.AddSingleton<NodePacketRouter>();
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IOptions<NodeConfig>>().Value;
            return new RequestCache<NodePacket>(config.RequestTimeoutMs);
        });
        services.AddSingleton<INodeSender, NodeSender>();
        services.AddSingleton<INodeResponser, NodeResponser>();
        services.AddSingleton<NodeCommunicator>();

        return services;
    }

    public static IServiceCollection AddNodeController<T>(this IServiceCollection services) where T : class
    {
        services.AddScoped<T>();
        return services;
    }

    /// <summary>
    /// Node Service Mesh용 미들웨어 등록 (생명주기 선택 가능)
    /// </summary>
    public static IServiceCollection AddNodeMiddleware<TMiddleware>(this IServiceCollection services, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TMiddleware : class, INodeMiddleware
    {
        services.Add(new ServiceDescriptor(typeof(INodeMiddleware), typeof(TMiddleware), lifetime));
        return services;
    }
}
