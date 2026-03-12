using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Game.Middleware;

namespace SimpleNetEngine.Game.Actor;

/// <summary>
/// SessionActor 생성 팩토리 인터페이스.
/// 기본 구현은 SessionActor를 생성하며,
/// 사용자가 상속하여 커스텀 Actor를 생성할 수 있다.
/// </summary>
public interface ISessionActorFactory
{
    ISessionActor Create(
        long actorId,
        long userId,
        long gatewayNodeId,
        IServiceScopeFactory scopeFactory,
        IMessageDispatcher dispatcher,
        MiddlewarePipeline pipeline,
        ILogger logger);
}

/// <summary>
/// 기본 SessionActor 팩토리 구현
/// </summary>
public class SessionActorFactory : ISessionActorFactory
{
    public virtual ISessionActor Create(
        long actorId,
        long userId,
        long gatewayNodeId,
        IServiceScopeFactory scopeFactory,
        IMessageDispatcher dispatcher,
        MiddlewarePipeline pipeline,
        ILogger logger)
    {
        return new SessionActor(actorId, userId, gatewayNodeId, scopeFactory, dispatcher, pipeline, logger);
    }
}
