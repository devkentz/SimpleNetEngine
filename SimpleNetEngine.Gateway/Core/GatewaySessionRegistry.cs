using System.Collections.Concurrent;
using SimpleNetEngine.Gateway.Network;

namespace SimpleNetEngine.Gateway.Core;

/// <summary>
/// Gateway 클라이언트 세션 레지스트리
/// SocketId(Guid) → GatewaySession 매핑 (기본 라우팅)
/// SessionId(long) → GatewaySession 매핑 (GameServer가 SocketId 없이 응답할 때 사용)
/// </summary>
public class GatewaySessionRegistry
{
    private readonly ConcurrentDictionary<Guid, GatewaySession> _sessions = new();
    private readonly ConcurrentDictionary<long, GatewaySession> _sessionsBySessionId = new();

    public bool TryGet(Guid socketId, out GatewaySession session)
        => _sessions.TryGetValue(socketId, out session!);

    public bool TryGetBySessionId(long sessionId, out GatewaySession session)
        => _sessionsBySessionId.TryGetValue(sessionId, out session!);

    public void Register(Guid socketId, GatewaySession session)
        => _sessions[socketId] = session;

    public void RegisterSessionId(long sessionId, GatewaySession session)
        => _sessionsBySessionId[sessionId] = session;

    public bool TryRemove(Guid socketId)
        => _sessions.TryRemove(socketId, out _);

    public void RemoveSessionId(long sessionId)
        => _sessionsBySessionId.TryRemove(sessionId, out _);

    public IEnumerable<GatewaySession> GetAll()
        => _sessions.Values;
}
