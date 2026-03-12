using Google.Protobuf;
using SimpleNetEngine.Protocol.Packets;
using SimpleNetEngine.Node.Core;

namespace SimpleNetEngine.Node.Network;

public interface INodeSender
{
    public Task<TResponse> RequestApiAsync<TRequest, TResponse>(long actorId, string apiName,
        TRequest request)
        where TRequest : IMessage<TRequest>, new()
        where TResponse : IMessage<TResponse>, new();

    public Task<TResponse> RequestApiAsync<TRequest, TResponse>(string apiName,
        TRequest request)
        where TRequest : IMessage<TRequest>, new()
        where TResponse : IMessage<TResponse>, new();
    
    public Task<TResponse> RequestAsync<TRequest, TResponse>(long actorId, long nodeId, TRequest request)
        where TRequest : IMessage<TRequest>, new()
        where TResponse : IMessage<TResponse>, new();
}

public interface INodeResponser
{
    public void Response(NodeHeader requestHeader, IMessage responseMessage);
}
                        

public class NodeResponser(INodeManager nodeManager, NodeCommunicator nodeCommunicator) : INodeResponser
{
    public void Response(NodeHeader requestHeader, IMessage responseMessage)
    {
        var response = NodePacket.Create(requestHeader.ActorId, requestHeader.Dest, requestHeader.Source, requestHeader.RequestKey, true, responseMessage);

        if (nodeManager.FindRemoteKey(response.Header.Dest) is not { } remoteKey)
            throw new Exception($"Remote key not found for nodeId: {response.Header.Dest}");

        nodeCommunicator.Send(remoteKey, response);
    }
} 
