using Microsoft.Extensions.Logging;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Node.Core;

/// <summary>
/// 단일 스레드 이벤트 루프 모델 (Node.js 스타일 async interleaving)
/// SingleThreadEventLoop + custom SynchronizationContext를 사용하여
/// await에서 양보 → 다음 패킷 즉시 처리 → continuation 복귀.
///
/// - 싱글 스레드 보장 (lock 불필요)
/// - await 시 이벤트 루프를 블로킹하지 않음 (HOL blocking 방지)
/// - 모든 continuation은 같은 이벤트 루프 스레드에서 실행
///
/// 주의: await 이후 상태가 변경될 수 있음. yield point 이후 mutable state 재검증 필요.
/// </summary>
public abstract class SequentialNodeEventHandler : NodeEventHandler, IDisposable
{
    private readonly SingleThreadEventLoop _eventLoop;

    protected SequentialNodeEventHandler(ILogger logger) : base(logger)
    {
        _eventLoop = new SingleThreadEventLoop(
            threadName: GetType().Name,
            onError: ex => _logger.LogError(ex, "Error processing node packet"));
    }

    public override void ProcessPacket(NodePacket packet)
    {
        _eventLoop.Schedule(() => _ = ProcessPacketSafeAsync(packet));
    }

    private async Task ProcessPacketSafeAsync(NodePacket packet)
    {
        try
        {
            await ProcessPacketInternalAsync(packet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing node packet");
        }
    }

    protected abstract Task ProcessPacketInternalAsync(NodePacket packet);

    public virtual void Dispose()
    {
        _eventLoop.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Per-Task 병렬 처리 모델
/// 모든 패킷을 독립된 Task로 fire-and-forget 실행합니다.
/// Stateless Service에서 사용 — 상태가 없으므로 순서 보장 불필요.
/// </summary>
public abstract class ParallelNodeEventHandler : NodeEventHandler
{
    protected ParallelNodeEventHandler(ILogger logger) : base(logger)
    {
    }

    public override void ProcessPacket(NodePacket packet)
    {
        _ = ProcessPacketSafeAsync(packet);
    }

    private async Task ProcessPacketSafeAsync(NodePacket packet)
    {
        try
        {
            await ProcessPacketInternalAsync(packet);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing stateless packet");
        }
    }

    protected abstract Task ProcessPacketInternalAsync(NodePacket packet);
}