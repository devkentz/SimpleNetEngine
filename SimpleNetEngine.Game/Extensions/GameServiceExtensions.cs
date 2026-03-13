using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using SimpleNetEngine.Game.Options;
using SimpleNetEngine.Game.Services;
using SimpleNetEngine.Game.Session;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Middleware;
using SimpleNetEngine.Game.Network;
using SimpleNetEngine.Game.Controllers;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Node.Extensions;
using SimpleNetEngine.Node.Core;
using SimpleNetEngine.Game.Generated;

namespace SimpleNetEngine.Game.Extensions;

/// <summary>
/// GameServer 빌더. AddGameServer()에서 사용.
/// 기본값이 합리적이므로 Configure()만 호출하면 동작.
/// </summary>
public sealed class GameServerBuilder
{
    internal Action<GameOptions>? ConfigureOptionsAction { get; private set; }
    internal Type EventHandlerType { get; private set; } = typeof(GameNodeEventHandler);
    internal Type ActorFactoryType { get; private set; } = typeof(SessionActorFactory);
    internal Type? LoginHandlerType { get; private set; }

    /// <summary>
    /// GameServer 옵션을 설정한다.
    /// </summary>
    public GameServerBuilder Configure(Action<GameOptions> configure)
    {
        ConfigureOptionsAction = configure;
        return this;
    }

    /// <summary>
    /// 커스텀 NodeEventHandler를 사용한다 (기본: GameNodeEventHandler).
    /// </summary>
    public GameServerBuilder UseEventHandler<T>() where T : GameNodeEventHandler
    {
        EventHandlerType = typeof(T);
        return this;
    }

    /// <summary>
    /// 커스텀 SessionActorFactory를 사용한다 (기본: SessionActorFactory).
    /// </summary>
    public GameServerBuilder UseActorFactory<T>() where T : class, ISessionActorFactory
    {
        ActorFactoryType = typeof(T);
        return this;
    }

    /// <summary>
    /// 앱 레벨 로그인 핸들러를 등록한다.
    /// </summary>
    public GameServerBuilder UseLoginHandler<T>() where T : class, ILoginHandler
    {
        LoginHandlerType = typeof(T);
        return this;
    }
}

public static class GameServiceExtensions
{
    /// <summary>
    /// GameServer 서비스를 DI 컨테이너에 등록
    /// </summary>
    public static IServiceCollection AddGameServices(
        this IServiceCollection services,
        Action<GameOptions> configureOptions)
    {
        // Options 패턴 사용
        services.Configure(configureOptions);

        // Redis 연결
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GameOptions>>().Value;
            return ConnectionMultiplexer.Connect(options.RedisConnectionString);
        });
        services.AddSingleton<IDatabase>(sp =>
            sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase());

        // 시간 추상화 (단위 테스트에서 FakeTimeProvider로 교체 가능)
        services.TryAddSingleton(TimeProvider.System);

        // 세션 저장소 (SSOT)
        services.AddSingleton<ISessionStore, RedisSessionStore>();

        // Kickout 발신 클라이언트 (Cross-Node Duplicate Login 시 사용)
        services.AddSingleton<KickoutMessageHandler>();

        // 게임 액터 매니저 + Dispose/Disconnect 대기열 + 공통 Disconnect 핸들러
        services.AddSingleton<ISessionActorManager, SessionActorManager>();
        services.AddSingleton<ActorDisposeQueue>();
        services.AddSingleton<GatewayDisconnectQueue>();
        services.AddSingleton<ActorDisconnectHandler>();

        // Middleware Pipeline (AOP) - Actor 내부에서 실행됨
        // 실행 순서: Exception → SequenceId → Logging → Performance
        services.AddSingleton<MiddlewarePipelineFactory>();
        services.AddUserMiddleware<ExceptionHandlingMiddleware>(ServiceLifetime.Singleton);
        services.AddUserMiddleware<SequenceIdMiddleware>(ServiceLifetime.Singleton);
        services.AddUserMiddleware<LoggingMiddleware>(ServiceLifetime.Singleton);
        services.AddUserMiddleware<PerformanceMiddleware>(ServiceLifetime.Singleton);

        // 핵심 서비스
        services.AddSingleton<GameServerHub>();
        services.AddSingleton<IClientPacketHandler>(sp => sp.GetRequiredService<GameServerHub>());

        services.AddSingleton<GameSessionChannelListener>();

        // 라이브러리 내장 UserController (Source Generator 미스캔 → 수동 등록)
        services.AddScoped<HandshakeController>();
        services.AddScoped<LoginController>();
        services.AddScoped<ReconnectController>();
        services.AddScoped<KickoutController>();
        services.AddScoped<HeartbeatController>();

        // HostedService 등록
        services.AddHostedService<GameHostedService>();
        services.AddHostedService<InactivityScanner>();
        services.AddHostedService<ActorDisposeService>();
        services.AddHostedService<GatewayDisconnectService>();

        return services;
    }

    /// <summary>
    /// 라이브러리 내장 핸들러를 MessageDispatcher에 등록
    /// Source Generator가 스캔하지 못하는 라이브러리 컨트롤러용.
    /// </summary>
    private static void RegisterBuiltInHandlers(MessageDispatcher dispatcher)
    {
        HandshakeController.RegisterHandlers(dispatcher);
        LoginController.RegisterHandlers(dispatcher);
        HeartbeatController.RegisterHandlers(dispatcher);
    }

    /// <summary>
    /// UserController + MessageDispatcher 등록.
    /// DI에 등록된 IUserHandlerRegistrar를 auto-discover하여 핸들러를 통합 등록한다.
    ///
    /// 사전 조건: AddGeneratedUserControllers()로 IUserHandlerRegistrar가 DI에 등록되어야 함.
    /// </summary>
    public static IServiceCollection AddUserControllers(this IServiceCollection services)
    {
        services.AddSingleton<IMessageDispatcher>(sp =>
        {
            var dispatcher = new MessageDispatcher();

            // 라이브러리 내장 핸들러 (HandshakeController, LoginController 등)
            RegisterBuiltInHandlers(dispatcher);

            // DI에서 모든 registrar를 가져와 핸들러 등록 (Source Generator auto-discover)
            foreach (var registrar in sp.GetServices<IUserHandlerRegistrar>())
            {
                registrar.RegisterHandlers(dispatcher);
            }

            return dispatcher;
        });

        return services;
    }

    /// <summary>
    /// GameSessionChannel용 미들웨어 등록 (생명주기 선택 가능)
    /// </summary>
    public static IServiceCollection AddUserMiddleware<TMiddleware>(this IServiceCollection services, ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TMiddleware : class, IPacketMiddleware
    {
        services.Add(new ServiceDescriptor(typeof(IPacketMiddleware), typeof(TMiddleware), lifetime));
        return services;
    }

    /// <summary>
    /// GameServer를 빌더 패턴으로 등록한다.
    /// 내부에서 AddGameServices + UseNode + AddNodeControllers + AddUserControllers를 자동 호출.
    ///
    /// 앱 레벨 UserController가 있으면 AddGeneratedUserControllers()를 별도 호출해야 함.
    /// (DI factory가 lazy이므로 호출 순서는 무관)
    /// </summary>
    public static IServiceCollection AddGameServer(
        this IServiceCollection services,
        Action<GameServerBuilder> configure)
    {
        var builder = new GameServerBuilder();
        configure(builder);

        var configureGame = builder.ConfigureOptionsAction
            ?? throw new InvalidOperationException("GameServerBuilder.Configure() must be called.");

        var gameOptions = new GameOptions();
        configureGame(gameOptions);

        var nodeConfig = gameOptions.ToNodeConfig();
        gameOptions.GameNodeId = nodeConfig.NodeId;

        // GameServer 서비스 등록
        services.AddGameServices(_ =>
        {
            configureGame(_);
            _.GameNodeId = gameOptions.GameNodeId;
        });

        // SessionActorFactory 등록
        services.AddSingleton(typeof(ISessionActorFactory), builder.ActorFactoryType);

        // LoginHandler 등록 (설정된 경우)
        if (builder.LoginHandlerType is not null)
        {
            services.AddScoped(typeof(ILoginHandler), builder.LoginHandlerType);
        }

        // Node Service Mesh
        services.UseNode(Microsoft.Extensions.Options.Options.Create(nodeConfig));

        // 라이브러리 내장 NodeController
        services.AddGeneratedNodeControllers();
        services.AddNodeControllers();

        // UserController + MessageDispatcher
        services.AddUserControllers();

        // NodeEventHandler
        services.AddSingleton(typeof(NodeEventHandler), builder.EventHandlerType);

        return services;
    }
}
