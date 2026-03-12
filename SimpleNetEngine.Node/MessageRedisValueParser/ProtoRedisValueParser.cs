using Google.Protobuf;
using StackExchange.Redis;

namespace SimpleNetEngine.Node.MessageRedisValueParser;

public class ProtoRedisValueParser : IMessageRedisValueParser
{
    public RedisValue ToRedisValue(IMessage message)
    {
        return message.ToByteArray();
    }

    public T? FromRedisValue<T>(RedisValue redisValue) where T : IMessage<T>, new()
    {
        try
        {
            var parser = new MessageParser<T>(() => new T());
            return parser.ParseFrom(redisValue);
        }
        catch (Exception )
        {
            return default;
        }
    }
}