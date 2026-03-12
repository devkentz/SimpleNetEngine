using System.Collections.Concurrent;

namespace SimpleNetEngine.Gateway.Core;

/// <summary>
/// Gateway의 유일한 상태: SocketId -> GameServerId 매핑
/// Dumb Proxy 원칙: 이 매핑 외에는 어떤 비즈니스 로직도 없음
/// </summary>
public class SessionMapper
{
    private readonly ConcurrentDictionary<Guid, long> _socketToGameServer = new();
    private readonly ConcurrentDictionary<Guid, long> _socketToSessionId = new();

    /// <summary>
    /// 소켓을 특정 GameServer에 고정
    /// GameServer가 인증 완료 후 Gateway에게 지시
    /// </summary>
    public void PinSocket(Guid socketId, long gameServerNodeId, long sessionId)
    {
        _socketToGameServer[socketId] = gameServerNodeId;
        _socketToSessionId[socketId] = sessionId;
    }

    /// <summary>
    /// 소켓이 어느 GameServer에 매핑되어 있는지 조회
    /// </summary>
    public bool TryGetGameServer(Guid socketId, out long gameServerNodeId, out long sessionId)
    {
        sessionId = 0;
        gameServerNodeId = 0;
        if (_socketToGameServer.TryGetValue(socketId, out gameServerNodeId))
        {
            _socketToSessionId.TryGetValue(socketId, out sessionId);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 소켓 연결 해제 시 매핑 제거
    /// </summary>
    public void RemoveSocket(Guid socketId)
    {
        _socketToGameServer.TryRemove(socketId, out _);
        _socketToSessionId.TryRemove(socketId, out _);
    }

    /// <summary>
    /// 통계: 현재 활성 소켓 수
    /// </summary>
    public int ActiveSocketCount => _socketToGameServer.Count;
}
