using Google.Protobuf;
using StackExchange.Redis;

namespace SimpleNetEngine.Node.MessageRedisValueParser;

public interface IMessageRedisValueParser
{
    RedisValue ToRedisValue(IMessage message);
    T? FromRedisValue<T>(RedisValue redisValue) where T : IMessage<T>, new();
}