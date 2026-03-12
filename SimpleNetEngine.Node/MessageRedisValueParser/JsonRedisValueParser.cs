using Google.Protobuf;
using StackExchange.Redis;

namespace SimpleNetEngine.Node.MessageRedisValueParser;

public class JsonRedisValueParser : IMessageRedisValueParser
{
    public RedisValue ToRedisValue(IMessage message)
    {
        return JsonFormatter.Default.Format(message);
    }

    public T? FromRedisValue<T>(RedisValue redisValue) where T : IMessage<T>, new()
    {
        try
        {
            return JsonParser.Default.Parse<T>(redisValue);
        }
        catch (Exception )
        {
            return default;
        }
    }
}