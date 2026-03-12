using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SimpleNetEngine.Game.Network;

/// <summary>
/// Gateway 소켓 해제 대기열.
/// Logout 등에서 응답 전송 후 Gateway disconnect를 즉시 보내면
/// 응답보다 disconnect가 먼저 도착하는 race condition 발생.
///
/// 대기열에 넣고 BackgroundService가 주기적으로 drain하여 disconnect 전송.
/// 클라이언트가 자연스럽게 TCP를 끊으면 ClientDisconnectedNtf 수신 시 대기열에서 제거.
/// </summary>
public class GatewayDisconnectQueue(ILogger<GatewayDisconnectQueue> logger)
{
    private readonly ConcurrentDictionary<long, long> _pending = new(); // sessionId → gatewayNodeId

    /// <summary>
    /// 대기 중인 disconnect 수
    /// </summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Gateway disconnect를 예약 (sessionId, gatewayNodeId)
    /// </summary>
    public void Schedule(long sessionId, long gatewayNodeId)
    {
        _pending[sessionId] = gatewayNodeId;
    }

    /// <summary>
    /// 클라이언트가 자연스럽게 disconnect한 경우 예약 취소
    /// </summary>
    /// <returns>true면 예약이 존재하여 취소됨</returns>
    public bool Cancel(long sessionId)
    {
        var removed = _pending.TryRemove(sessionId, out _);
        if (removed)
        {
            logger.LogDebug(
                "Gateway disconnect cancelled (client disconnected first): SessionId={SessionId}",
                sessionId);
        }
        return removed;
    }

    /// <summary>
    /// 대기 중인 모든 disconnect를 꺼냄.
    /// 반환: (sessionId, gatewayNodeId) 목록
    /// </summary>
    public List<(long SessionId, long GatewayNodeId)> DrainAll()
    {
        if (_pending.IsEmpty)
            return [];
        
        var items = new List<(long, long)>();

        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var gatewayNodeId))
                items.Add((kvp.Key, gatewayNodeId));
        }

        return items;
    }
}
