using Microsoft.Extensions.DependencyInjection;

namespace SimpleNetEngine.Game.Middleware;

/// <summary>
/// DI 기반의 Middleware Pipeline 실행기
/// ASP.NET Core RequestDelegate 패턴: 빌드 타임에 delegate chain을 프리컴파일하여
/// per-request 클로저/델리게이트/state machine 할당을 제거합니다.
/// </summary>
public class MiddlewarePipeline
{
    private readonly Func<PacketContext, Task> _compiled;

    public MiddlewarePipeline(IEnumerable<IPacketMiddleware> middlewares)
    {
        // Terminal: 아무것도 하지 않는 종단점
        Func<PacketContext, Task> pipeline = static _ => Task.CompletedTask;

        // 역순으로 감싸서 delegate chain 구성 (빌드 타임 1회만 실행)
        foreach (var middleware in middlewares.Reverse())
        {
            var next = pipeline;
            pipeline = ctx => middleware.InvokeAsync(ctx, () => next(ctx));
        }

        _compiled = pipeline;
    }

    /// <summary>
    /// Pipeline 실행 — per-request 할당 없음
    /// </summary>
    public Task ExecuteAsync(IServiceProvider scope, PacketContext context)
        => _compiled(context);
}

/// <summary>
/// Middleware Pipeline Factory
/// </summary>
public class MiddlewarePipelineFactory
{
    private readonly IEnumerable<IPacketMiddleware> _middlewares;

    public MiddlewarePipelineFactory(IEnumerable<IPacketMiddleware> middlewares)
    {
        _middlewares = middlewares;
    }

    public MiddlewarePipeline CreateDefaultPipeline()
    {
        return new MiddlewarePipeline(_middlewares);
    }
}
