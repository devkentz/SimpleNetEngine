using Internal.Protocol;
using Microsoft.Extensions.Options;
using SimpleNetEngine.Node.Config;
using SimpleNetEngine.Node.MessageRedisValueParser;
using StackExchange.Redis;

namespace SimpleNetEngine.Node.Cluster;

public class RedisClusterRegistry : IClusterRegistry, IDisposable
{
    private readonly IConnectionMultiplexer _connection;
    private readonly IDatabase _database;
    private readonly NodeConfig _config;
    private readonly IMessageRedisValueParser _redisValueParser;
    private bool _disposed;

    // IOptions<NodeConfig>를 주입받아 내부에서 .Value를 사용하도록 수정
    public RedisClusterRegistry(IMessageRedisValueParser redisValueParser, IOptions<NodeConfig> config)
    {
        _config = config.Value;

        if (string.IsNullOrEmpty(_config.RedisConnectionString))
        {
            throw new InvalidOperationException("Redis connection string ('Node:RedisConnectionString') must be configured.");
        }

        // 전용 Redis 연결 생성
        _connection = ConnectionMultiplexer.Connect(_config.RedisConnectionString);
        _database = _connection.GetDatabase();
        _redisValueParser = redisValueParser;
    }

    public void Dispose()
    {
        if (_disposed) 
            return;
        
        _disposed = true;
        _connection.Dispose();
    }
    
    public async Task<List<ServerInfo>> RegisterAndGetOtherNodesAsync(ServerInfo selfInfo, TimeSpan ttl)
    {
        const string script = """
                              redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
                              return redis.call('HGETALL', KEYS[1])
                              """;
        
 
        var selfIdVal = selfInfo.RemoteId;
        var selfInfoVal = _redisValueParser.ToRedisValue(selfInfo);

        var result = await _database.ScriptEvaluateAsync(script, [_config.ServerRegistryKey], [selfIdVal, selfInfoVal]);

        // TTL 설정은 별도로 수행 (Lua 내에서 처리하거나 확장 메서드 사용)
        // 여기서는 기존 로직과의 호환성을 위해 별도 호출 유지
        await _database.HashFieldExpireAsync(_config.ServerRegistryKey, [selfInfo.RemoteId], ttl);

        var serverInfos = new List<ServerInfo>();
        // HGETALL returns array of [key, value, key, value, ...]
        for (int i = 0; i < result.Length; i += 2)
        {
            // key = result[i], value = result[i+1]
            // We only need value to parse ServerInfo, but we might check key if needed.
            var val = result[i + 1];
            var info = _redisValueParser.FromRedisValue<ServerInfo>(val.ToString());
            if (info != null && info.RemoteId != selfInfo.RemoteId)
            {
                serverInfos.Add(info);
            }
        }

        return serverInfos;
    }

    public async Task<List<ServerInfo>> GetOtherLiveNodesAsync(long selfNodeId)
    {
        var entries = await _database.HashGetAllAsync(_config.ServerRegistryKey);
        var serverInfos = new List<ServerInfo>();
        foreach (var entry in entries)
        {
            var info = _redisValueParser.FromRedisValue<ServerInfo>(entry.Value);
            if (info != null && info.RemoteId != selfNodeId)
            {
                serverInfos.Add(info);
            }
        }

        return serverInfos;
    }

    public async Task RegisterSelfAsync(ServerInfo selfInfo, TimeSpan ttl)
    {
        await _database.HashSetAsync(_config.ServerRegistryKey, selfInfo.RemoteId, _redisValueParser.ToRedisValue(selfInfo));
        // 개별 필드에 TTL을 설정하는 것으로 추정되는 확장 메서드 사용
        await _database.HashFieldExpireAsync(_config.ServerRegistryKey, [selfInfo.RemoteId], ttl);
    }

    public Task UpdateHeartbeatAsync(long selfNodeId, TimeSpan ttl)
    {
        // 개별 필드에 TTL을 설정하는 것으로 추정되는 확장 메서드 사용
        return _database.HashFieldExpireAsync(_config.ServerRegistryKey, [selfNodeId], ttl);
    }

    public async Task<HashSet<long>> GetLiveNodeIdsAsync()
    {
        // HashKeysAsync는 필드에 TTL이 걸린 경우 만료된 키를 반환하지 않으므로, 이 방식이 유효합니다.
        var keys = await _database.HashKeysAsync(_config.ServerRegistryKey);
        return keys.Select(k => (long) k).ToHashSet();
    }

    public async Task<ServerInfo?> GetNodeInfoAsync(long nodeId)
    {
        var value = await _database.HashGetAsync(_config.ServerRegistryKey, nodeId);
        if (!value.HasValue)
        {
            return null;
        }

        return _redisValueParser.FromRedisValue<ServerInfo>(value);
    }

    public Task UnregisterSelfAsync(long selfNodeId)
    {
        return _database.HashDeleteAsync(_config.ServerRegistryKey, selfNodeId);
    }
}