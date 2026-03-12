using System;
using System.Collections.Generic;
using Google.Protobuf;

namespace SimpleNetEngine.Client.Network;

public class MessageHandler
{
    private readonly Dictionary<long, Action<IMessage>> _handlers = new();

    protected void AddHandler<TRequest>(
        long msgId,
        Action<TRequest> func)
        where TRequest : class, IMessage, new()
    {
        _handlers[msgId] = (message) =>
        {
            if (message is not TRequest request)
                throw new InvalidCastException($"Cannot cast {message.GetType().Name} to {typeof(TRequest).Name}");

            func.Invoke(request);
        };
    }

    public virtual void Handling(NetworkPacket packet)
    {
        if (_handlers.TryGetValue(packet.GameHeader.MsgId, out var handler))
        {
            handler.Invoke(packet.Message);
            return;            
        }

        throw new Exception($"No handler registered for MsgId: {packet.GameHeader.MsgId}");
    }

    public void Initialize()
    {
        LoadHandlers();
    }

    protected virtual void LoadHandlers()
    {
    }
}