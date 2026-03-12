using System.Collections.Concurrent;

namespace SimpleNetEngine.Node.Core;

/// <summary>
/// 서비스 메시(Node) 계층에서 사용하는 Actor 관리 인터페이스.
/// NodeActor(백엔드 RPC Actor)의 등록/조회/삭제를 담당한다.
/// </summary>
public interface INodeActorManager
{
    INodeActor? FindActor(long actorId);
    void AddActor(INodeActor actor);
    void RemoveActor(long actorId);
}

/// <summary>
/// INodeActorManager 기본 구현체.
/// ConcurrentDictionary 기반 thread-safe 관리.
/// </summary>
public class NodeActorManager : INodeActorManager
{
    private readonly ConcurrentDictionary<long, INodeActor> _actorsById = new();

    public INodeActor? FindActor(long actorId)
    {
        return _actorsById.GetValueOrDefault(actorId);
    }

    public void AddActor(INodeActor actor)
    {
        _actorsById.TryAdd(actor.ActorId, actor);
    }

    public void RemoveActor(long actorId)
    {
        _actorsById.TryRemove(actorId, out _);
    }
}
