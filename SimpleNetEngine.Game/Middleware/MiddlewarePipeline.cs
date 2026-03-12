using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SimpleNetEngine.Game.Middleware;

/// <summary>
/// DI 기반의 Middleware Pipeline 실행기
/// 매 요청마다 IServiceProvider에서 등록된 미들웨어를 가져와 실행하여 Scoped 생명주기를 지원합니다.
/// </summary>
public class MiddlewarePipeline
{
    private readonly ILogger<MiddlewarePipeline> _logger;

    public MiddlewarePipeline(ILogger<MiddlewarePipeline> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Pipeline 실행
    /// </summary>
    /// <param name="scope">현재 요청의 DI Scope</param>
    /// <param name="context">패킷 컨텍스트</param>
    public async Task ExecuteAsync(IServiceProvider scope, PacketContext context)
    {
        // 등록된 모든 IPacketMiddleware 서비스를 가져옴 (DI에 등록된 순서대로 반환됨)
        var middlewares = scope.GetServices<IPacketMiddleware>().ToList();
        
        if (middlewares.Count == 0) return;

        var index = 0;

        async Task Next()
        {
            if (index < middlewares.Count)
            {
                var middleware = middlewares[index++];
                await middleware.InvokeAsync(context, Next);
            }
        }

        await Next();
    }
}

/// <summary>
/// Middleware Pipeline Factory
/// </summary>
public class MiddlewarePipelineFactory
{
    private readonly ILogger<MiddlewarePipeline> _pipelineLogger;

    public MiddlewarePipelineFactory(ILogger<MiddlewarePipeline> pipelineLogger)
    {
        _pipelineLogger = pipelineLogger;
    }

    public MiddlewarePipeline CreateDefaultPipeline()
    {
        return new MiddlewarePipeline(_pipelineLogger);
    }
}
