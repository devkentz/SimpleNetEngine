using System.Collections.Concurrent;

namespace SimpleNetEngine.Node.Utils
{
    public class RequestCache<T>(int timeoutMs) : IDisposable
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<T>> _cache = new();
        private int _sequence = 0;

        // 통계
        private long _totalRequests;
        private long _successCount;
        private long _timeoutCount;
        private long _failedCount;

        public int GetRequestKey()
        {
            Interlocked.Increment(ref _totalRequests);
            return Interlocked.Increment(ref _sequence);
        }

        public void TryReply(int key, T item)
        {
            if (!_cache.TryRemove(key, out var tcs))
                return;

            if (tcs.TrySetResult(item))
                Interlocked.Increment(ref _successCount);
        }

        public bool TryFail(int requestKey, Exception exception)
        {
            // 1. 캐시에서 TaskCompletionSource 찾기
            if (!_cache.TryGetValue(requestKey, out var tcs))
            {
                // 이미 timeout으로 제거되었거나 응답 완료된 경우
                return false;
            }

            // 2. 예외로 Task 완료 시도
            if (tcs.TrySetException(exception))
            {
                // 3. 통계 업데이트
                Interlocked.Increment(ref _failedCount);

                // 4. 캐시에서 제거 (cleanup)
                _cache.TryRemove(requestKey, out _);

                return true;
            }

            return false;
        }

        public async ValueTask<T> PendingAsync(
            int requestKey,
            CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            _cache[requestKey] = tcs;

            using var timeoutCts = new CancellationTokenSource(timeoutMs);

            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeoutCts.Token);

                return await tcs.Task.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                Interlocked.Increment(ref _timeoutCount);
                throw new TimeoutException(
                    $"Request {requestKey} timed out after {timeoutMs}ms");
            }
            finally
            {
                _cache.TryRemove(requestKey, out _);
            }
        }

        public (long Total, long Success, long Timeout, long Failed, int Pending) GetStats()
        {
            return (
                Interlocked.Read(ref _totalRequests),
                Interlocked.Read(ref _successCount),
                Interlocked.Read(ref _timeoutCount),
                Interlocked.Read(ref _failedCount),
                _cache.Count
            );
        }

        public void Dispose()
        {
            foreach (var kvp in _cache)
            {
                kvp.Value.TrySetCanceled();
            }

            _cache.Clear();
        }
    }
}