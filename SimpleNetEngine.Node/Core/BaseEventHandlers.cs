using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Node.Core;

/// <summary>
/// 단일 스레드 순차 처리 모델 (Sequential / Single-Thread)
/// 모든 요청을 전역 큐에 담아 순차적으로 하나씩 처리합니다.
/// ActorId를 무시하고 모든 패킷을 하나의 큐로 직렬화합니다.
/// </summary>
public abstract class SequentialNodeEventHandler : NodeEventHandler
{
    private readonly QueuedResponseWriter<NodePacket> _sequentialQueue;

    protected SequentialNodeEventHandler(ILogger logger) : base(logger)
    {
        _sequentialQueue = new QueuedResponseWriter<NodePacket>(ProcessPacketInternalAsync, logger);
    }

    public override void ProcessPacket(NodePacket packet)
    {
        _sequentialQueue.Write(packet);
    }

    protected abstract Task ProcessPacketInternalAsync(NodePacket packet);
}

/// <summary>
/// 병렬 처리 모델 (Parallel)
/// - ActorId == 0: fire-and-forget 병렬 실행
/// - ActorId != 0: per-ActorId(ServerId) 큐로 같은 ID 내 순차, 다른 ID 간 병렬 처리
/// Stateless Service에서 주로 사용됩니다.
/// </summary>
public abstract class ParallelNodeEventHandler : NodeEventHandler
{
    private readonly ConcurrentDictionary<long, QueuedResponseWriter<NodePacket>> _actorQueues = new();

    protected ParallelNodeEventHandler(ILogger logger) : base(logger)
    {
    }

    public override void ProcessPacket(NodePacket packet)
    {
        var actorId = packet.Header.ActorId;
        if (actorId == 0)
        {
            _ = ProcessPacketInternalAsync(packet);
            return;
        }

        var queue = _actorQueues.GetOrAdd(actorId,
            _ => new QueuedResponseWriter<NodePacket>(ProcessPacketInternalAsync, _logger));
        queue.Write(packet);
    }

    protected abstract Task ProcessPacketInternalAsync(NodePacket packet);
}

/// <summary>
/// 액터 기반 직렬화 모델 (Actor-Serialized)
/// INodeActorManager를 통해 Actor를 조회하고 actor.Push(packet)으로 전달합니다.
/// 각 Actor가 자체 QueuedResponseWriter 메일박스를 소유하여 per-ActorId 순차 처리를 보장합니다.
/// </summary>
public abstract class ActorNodeEventHandler : NodeEventHandler
{
    private readonly INodeActorManager _actorManager;

    protected ActorNodeEventHandler(ILogger logger, INodeActorManager actorManager) : base(logger)
    {
        _actorManager = actorManager;
    }

    public override void ProcessPacket(NodePacket packet)
    {
        var actorId = packet.Header.ActorId;
        var actor = _actorManager.FindActor(actorId);
        if (actor == null)
        {
            OnActorNotFound(packet, actorId);
            return;
        }

        actor.Push(packet);
    }

    /// <summary>
    /// Actor를 찾지 못했을 때 호출되는 가상 메서드 (기본: 경고 로그 + 패킷 해제)
    /// </summary>
    protected virtual void OnActorNotFound(NodePacket packet, long actorId)
    {
        _logger.LogWarning("Actor not found: ActorId={ActorId}, MsgId={MsgId}", actorId, packet.Header.MsgId);
        packet.Dispose();
    }
}
