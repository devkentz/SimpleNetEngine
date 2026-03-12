using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Protocol.Packets;
using SimpleNetEngine.Infrastructure.Middleware;

namespace SimpleNetEngine.Node.Core;

/// <summary>
/// Node Controller 디스패처 구현
/// MsgId -> Handler 매핑 및 INodeMiddleware 파이프라인 실행
/// </summary>
public class NodeDispatcher(ILogger<NodeDispatcher> logger) : INodeDispatcher
{
    private readonly Dictionary<int, Func<IServiceProvider, NodePacket, Task<IMessage?>>> _handlers = [];

    public void RegisterHandler(int msgId, Func<IServiceProvider, NodePacket, Task<IMessage?>> handler)
    {
        _handlers[msgId] = handler;
        logger.LogDebug("NodeDispatcher: Registered handler for MsgId={MsgId}", msgId);
    }

    public async Task<IMessage?> DispatchAsync(IServiceProvider scope, NodePacket packet)
    {
        if (!_handlers.TryGetValue(packet.Header.MsgId, out var handler))
        {
            logger.LogWarning("NodeDispatcher: No handler for MsgId={MsgId}", packet.Header.MsgId);
            return null;
        }

        // 1. 현재 Scope에서 미들웨어 목록 획득 (생명주기 존중)
        var middlewares = scope.GetServices<INodeMiddleware>().ToArray();

        if (middlewares.Length == 0)
        {
            return await handler(scope, packet);
        }

        // 2. 미들웨어 파이프라인 구성 및 실행
        return await ExecutePipeline(scope, packet, handler, middlewares, 0);
    }

    private async Task<IMessage?> ExecutePipeline(
        IServiceProvider scope,
        NodePacket packet,
        Func<IServiceProvider, NodePacket, Task<IMessage?>> handler,
        INodeMiddleware[] middlewares,
        int index)
    {
        if (index >= middlewares.Length)
        {
            return await handler(scope, packet);
        }

        var middleware = middlewares[index];
        return await middleware.InvokeAsync(packet, async (p) => 
            await ExecutePipeline(scope, p, handler, middlewares, index + 1));
    }

    public bool HasHandler(int msgId) => _handlers.ContainsKey(msgId);
}
