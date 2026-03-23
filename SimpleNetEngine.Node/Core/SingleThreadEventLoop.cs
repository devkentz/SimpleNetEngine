using System.Collections.Concurrent;

namespace SimpleNetEngine.Node.Core;

/// <summary>
/// 단일 스레드 이벤트 루프 (Node.js 스타일 async interleaving)
///
/// QueuedResponseWriter와의 차이:
/// - QueuedResponseWriter: await 완료까지 다음 항목 대기 (strict sequential, HOL blocking 발생)
/// - SingleThreadEventLoop: await에서 양보 → 다음 작업 즉시 처리 → continuation 복귀 (async interleaving)
///
/// custom SynchronizationContext를 설정하여 모든 await continuation이
/// 같은 이벤트 루프 스레드에 마샬링됩니다. 싱글 스레드 보장 + 블로킹 없는 동시 진행.
/// </summary>
public sealed class SingleThreadEventLoop : IDisposable
{
    private readonly BlockingCollection<Action> _workQueue = [];
    private readonly Thread _thread;
    private readonly Action<Exception>? _onError;
    private int _isDisposed;

    public SingleThreadEventLoop(string? threadName = null, Action<Exception>? onError = null)
    {
        _onError = onError;
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = threadName ?? "SingleThreadEventLoop"
        };
        _thread.Start();
    }

    /// <summary>
    /// 이벤트 루프에 작업을 예약합니다. 스레드 안전합니다.
    /// ExecutionContext를 캡처하여 AsyncLocal 등의 흐름을 유지합니다.
    /// </summary>
    public void Schedule(Action action)
    {
        if (_workQueue.IsAddingCompleted) return;

        // Schedule 직접 호출 시에도 AsyncLocal 컨텍스트 유지
        var executionContext = ExecutionContext.Capture();
        Action contextualAction = executionContext != null
            ? () => ExecutionContext.Run(executionContext, _ => action(), null)
            : action;

        try
        {
            _workQueue.Add(contextualAction);
        }
        catch (InvalidOperationException) { /* race with CompleteAdding */ }
    }

    private void RunLoop()
    {
        SynchronizationContext.SetSynchronizationContext(new EventLoopSyncContext(this));

        try
        {
            foreach (var action in _workQueue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    try
                    {
                        _onError?.Invoke(ex);
                    }
                    catch
                    {
                        // _onError 내부 예외 발생 시 루프 크래시 방지
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1) return;

        _workQueue.CompleteAdding();

        // 루프 스레드 내부에서 Dispose가 호출된 경우 데드락 방지
        bool isLoopThread = Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

        if (!isLoopThread)
        {
            bool gracefullyStopped = _thread.Join(TimeSpan.FromSeconds(5));
            if (gracefullyStopped)
            {
                _workQueue.Dispose();
            }
        }
    }

    private sealed class EventLoopSyncContext(SingleThreadEventLoop loop) : SynchronizationContext
    {
        // await 상태 기계는 자체적으로 ExecutionContext를 복원하므로 여기선 바로 스케줄링
        public override void Post(SendOrPostCallback d, object? state)
            => loop.Schedule(() => d(state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Environment.CurrentManagedThreadId == loop._thread.ManagedThreadId)
            {
                d(state);
                return;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try { d(state); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, null);

            tcs.Task.GetAwaiter().GetResult();
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
