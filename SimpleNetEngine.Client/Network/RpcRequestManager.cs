using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace SimpleNetEngine.Client.Network;

/// <summary>
/// RPC 요청-응답 관리자
/// RequestId 기반 비동기 요청 처리 담당
/// </summary>
public class RpcRequestManager : IDisposable
{
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<IMessage>> _pendingRequests = [];
    private readonly IRequestIdGenerator _idGenerator;
    private readonly TimeSpan _timeout;

    public RpcRequestManager(IRequestIdGenerator idGenerator, TimeSpan timeout)
    {
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _timeout = timeout;
    }

    /// <summary>
    /// 비동기 RPC 요청 전송 및 응답 대기
    /// </summary>
    /// <param name="sendAction">RequestId와 함께 메시지를 전송하는 액션</param>
    /// <param name="cancellationToken">취소 토큰</param>
    /// <returns>응답 메시지</returns>
    public async Task<IMessage> SendRequestAsync(
        Action<ushort> sendAction,
        CancellationToken cancellationToken = default)
    {
        var requestId = _idGenerator.Next();
        var tcs = new TaskCompletionSource<IMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(requestId, tcs))
        {
            throw new InvalidOperationException($"RequestId collision: {requestId}");
        }

        using var timeoutCts = new CancellationTokenSource(_timeout);

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            sendAction(requestId);

            return await tcs.Task
                .WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Request timed out after {_timeout.TotalMilliseconds}ms (RequestId: {requestId})"
            );
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// 응답 메시지로 대기 중인 요청 완료
    /// </summary>
    /// <param name="requestId">요청 ID</param>
    /// <param name="response">응답 메시지</param>
    /// <returns>요청이 존재하여 완료되었으면 true</returns>
    public bool TryCompleteRequest(ushort requestId, IMessage response)
    {
        if (_pendingRequests.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(response);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 모든 대기 중인 요청 취소 (연결 종료 시 호출)
    /// </summary>
    public void CancelAll()
    {
        foreach (var tcs in _pendingRequests.Values)
            tcs.TrySetCanceled();

        _pendingRequests.Clear();
    }

    /// <summary>
    /// 현재 대기 중인 요청 수
    /// </summary>
    public int PendingCount => _pendingRequests.Count;

    public void Dispose()
    {
        CancelAll();
    }
}
