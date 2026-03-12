using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SimpleNetEngine.Game.Actor;

/// <summary>
/// Actor Dispose 대기열.
/// Actor mailbox 내부에서 자기 자신을 Dispose하면 self-deadlock이 발생하므로,
/// Dispose 대상을 큐에 넣고 외부 BackgroundService가 주기적으로 Drain하여 Dispose한다.
///
/// 사용처:
/// - HandleLogout: Actor mailbox 안에서 실행 → RemoveActor 대신 UnregisterActor + Enqueue
/// - InactivityScanner: actor.ExecuteAsync 안에서 실행 → 동일 (비활성 감지 + Grace Period 만료 포함)
/// </summary>
public class ActorDisposeQueue(ILogger<ActorDisposeQueue> logger)
{
    private readonly ConcurrentQueue<ISessionActor> _queue = new();

    /// <summary>
    /// 대기 중인 Actor 수
    /// </summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    /// Dispose 대상 Actor를 큐에 추가 (non-blocking, thread-safe)
    /// </summary>
    public void Enqueue(ISessionActor actor)
    {
        _queue.Enqueue(actor);
    }

    /// <summary>
    /// 큐의 모든 Actor를 꺼내서 Dispose.
    /// 개별 Dispose 실패는 로깅 후 계속 진행.
    /// 반환값: Dispose 처리한 Actor 수.
    /// </summary>
    public int DrainAndDispose()
    {
        var count = 0;

        while (_queue.TryDequeue(out var actor))
        {
            count++;
            try
            {
                actor.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Actor dispose failed: ActorId={ActorId}, UserId={UserId}",
                    actor.ActorId, actor.UserId);
            }
        }

        return count;
    }
}
