namespace SimpleNetEngine.Game.Session;

/// <summary>
/// 세션 저장소 인터페이스 (Redis SSOT)
/// 유저 ID → (GameServerNodeId, SessionId, GatewayNodeId) 매핑
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// 세션 정보 조회
    /// </summary>
    /// <param name="userId">유저 ID</param>
    /// <returns>세션 정보 (없으면 null)</returns>
    Task<SessionInfo?> GetSessionAsync(long userId);

    /// <summary>
    /// 세션 정보 저장 (신규 로그인 또는 갱신)
    /// </summary>
    /// <param name="userId">유저 ID</param>
    /// <param name="sessionInfo">세션 정보</param>
    /// <param name="ttl">TTL (Time To Live) - null이면 무제한</param>
    Task SetSessionAsync(long userId, SessionInfo sessionInfo, TimeSpan? ttl = null);

    /// <summary>
    /// 세션 정보 삭제 (로그아웃)
    /// </summary>
    /// <param name="userId">유저 ID</param>
    Task DeleteSessionAsync(long userId);

    /// <summary>
    /// 세션 정보 조건부 삭제: Redis의 SessionId가 expectedSessionId와 일치할 때만 삭제.
    /// Cross-Node Kickout 시 새 노드가 이미 덮어쓴 경우 삭제를 방지.
    /// </summary>
    /// <returns>삭제 성공 여부</returns>
    Task<bool> DeleteSessionIfMatchAsync(long userId, long expectedSessionId);

    /// <summary>
    /// 세션이 특정 GameServer에 존재하는지 확인
    /// </summary>
    /// <param name="userId">유저 ID</param>
    /// <param name="gameServerNodeId">GameServer NodeId</param>
    /// <returns>해당 GameServer에 세션이 있으면 true</returns>
    Task<bool> IsSessionOnNodeAsync(long userId, long gameServerNodeId);

    /// <summary>
    /// ReconnectKey로 UserId 역인덱스 조회 (재접속 시 사용)
    /// </summary>
    /// <param name="reconnectKey">재접속 토큰</param>
    /// <returns>UserId (없으면 null)</returns>
    Task<long?> GetUserIdByReconnectKeyAsync(Guid reconnectKey);

    /// <summary>
    /// ReconnectKey 역인덱스 저장 (로그인/재접속 성공 시)
    /// </summary>
    Task SetReconnectKeyAsync(Guid reconnectKey, long userId, TimeSpan? ttl = null);

    /// <summary>
    /// ReconnectKey 역인덱스 삭제 (로그아웃/Kickout/만료 시)
    /// </summary>
    Task DeleteReconnectKeyAsync(Guid reconnectKey);
}

/// <summary>
/// 세션 정보 (Redis에 저장되는 데이터)
/// </summary>
public record SessionInfo
{
    /// <summary>
    /// GameServer NodeId (어느 GameServer에 로그인했는지)
    /// </summary>
    public long GameServerNodeId { get; init; }

    /// <summary>
    /// 게임 세션 ID
    /// </summary>
    public long SessionId { get; init; }

    /// <summary>
    /// Gateway NodeId (어느 Gateway를 통해 연결되었는지)
    /// </summary>
    public long GatewayNodeId { get; init; }

    /// <summary>
    /// 세션 생성 시각 (UTC)
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// 마지막 활동 시각 (UTC)
    /// </summary>
    public DateTimeOffset LastActivityUtc { get; init; }
}
