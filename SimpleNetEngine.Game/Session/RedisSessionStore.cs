using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace SimpleNetEngine.Game.Session;

/// <summary>
/// Redis 기반 세션 저장소
/// SSOT (Single Source Of Truth) 구현
/// </summary>
public class RedisSessionStore : ISessionStore
{
    private readonly ILogger<RedisSessionStore> _logger;
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    private const string KeyPrefix = "session:";
    private const string ReconnectKeyPrefix = "session:reconnect:";
    private static readonly TimeSpan DefaultTTL = TimeSpan.FromHours(24);

    public RedisSessionStore(
        ILogger<RedisSessionStore> logger,
        IConnectionMultiplexer redis)
    {
        _logger = logger;
        _redis = redis;
        _db = redis.GetDatabase();
    }

    public async Task<SessionInfo?> GetSessionAsync(long userId)
    {
        var key = GetKey(userId);
        var value = await _db.StringGetAsync(key);

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        var sessionInfo = JsonSerializer.Deserialize<SessionInfo>(value!);
        return sessionInfo;
    }

    public async Task SetSessionAsync(long userId, SessionInfo sessionInfo, TimeSpan? ttl = null)
    {
        var key = GetKey(userId);
        var value = JsonSerializer.Serialize(sessionInfo);
        var expiry = ttl ?? DefaultTTL;

        await _db.StringSetAsync(key, value, expiry);

        _logger.LogDebug("Session saved to Redis: UserId={UserId}, GameServerNodeId={NodeId}, SessionId={SessionId}",
            userId, sessionInfo.GameServerNodeId, sessionInfo.SessionId);
    }

    public async Task DeleteSessionAsync(long userId)
    {
        var key = GetKey(userId);
        await _db.KeyDeleteAsync(key);

        _logger.LogDebug("Session deleted from Redis: UserId={UserId}", userId);
    }

    /// <summary>
    /// 조건부 삭제: Redis의 SessionId가 expectedSessionId와 일치할 때만 삭제.
    /// Lua script로 GET + 비교 + DEL을 atomic하게 수행.
    /// </summary>
    public async Task<bool> DeleteSessionIfMatchAsync(long userId, long expectedSessionId)
    {
        var key = GetKey(userId);

        var result = await _db.ScriptEvaluateAsync(
            DeleteIfMatchScript,
            [(RedisKey)key],
            [(RedisValue)expectedSessionId.ToString()]);

        var deleted = (int)result == 1;

        _logger.LogDebug(
            "Session conditional delete: UserId={UserId}, ExpectedSessionId={ExpectedSessionId}, Deleted={Deleted}",
            userId, expectedSessionId, deleted);

        return deleted;
    }

    /// <summary>
    /// Lua script: JSON 값에서 SessionId를 추출하여 비교 후 일치하면 삭제.
    /// </summary>
    private const string DeleteIfMatchScript =
        """
        local val = redis.call('GET', KEYS[1])
        if not val then return 0 end
        local sid = string.match(val, '"SessionId":(%d+)')
        if sid == ARGV[1] then
            redis.call('DEL', KEYS[1])
            return 1
        end
        return 0
        """;

    /// <summary>
    /// 세션이 특정 GameServer 노드에 있는지 확인
    /// Redis 장애 시 예외 전파 (false와 구분하기 위해)
    /// </summary>
    public async Task<bool> IsSessionOnNodeAsync(long userId, long gameServerNodeId)
    {
        var sessionInfo = await GetSessionAsync(userId);
        return sessionInfo?.GameServerNodeId == gameServerNodeId;
    }

    public async Task<long?> GetUserIdByReconnectKeyAsync(Guid reconnectKey)
    {
        var key = GetReconnectKey(reconnectKey);
        var value = await _db.StringGetAsync(key);

        if (value.IsNullOrEmpty)
            return null;

        if (long.TryParse(value!, out var userId))
            return userId;

        _logger.LogWarning("Invalid userId in reconnect key: Key={Key}, Value={Value}", key, value);
        return null;
    }

    public async Task SetReconnectKeyAsync(Guid reconnectKey, long userId, TimeSpan? ttl = null)
    {
        var key = GetReconnectKey(reconnectKey);
        var expiry = ttl ?? DefaultTTL;
        await _db.StringSetAsync(key, userId.ToString(), expiry);

        _logger.LogDebug("ReconnectKey saved: Key={Key}, UserId={UserId}", reconnectKey, userId);
    }

    public async Task DeleteReconnectKeyAsync(Guid reconnectKey)
    {
        var key = GetReconnectKey(reconnectKey);
        await _db.KeyDeleteAsync(key);

        _logger.LogDebug("ReconnectKey deleted: Key={Key}", reconnectKey);
    }

    private static string GetKey(long userId)
    {
        return $"{KeyPrefix}{userId}";
    }

    private static string GetReconnectKey(Guid reconnectKey)
    {
        return $"{ReconnectKeyPrefix}{reconnectKey}";
    }
}
