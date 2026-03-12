using SimpleNetEngine.Protocol.Packets;
using SimpleNetEngine.Node.Core;
using SimpleNetEngine.Node.Utils;

namespace SimpleNetEngine.Node.Network;

public class NodePacketRouter
{
    private readonly RequestCache<NodePacket> _requestCache;
    private readonly NodeEventHandler _handler;

    public NodePacketRouter(RequestCache<NodePacket> requestCache, NodeEventHandler handler, NodeCommunicator nodeCommunicator)
    {
        _requestCache = requestCache;
        _handler = handler;

        nodeCommunicator.OnProcessPacket += OnProcessPacket;
        nodeCommunicator.OnSendFailed += OnSendFailed;
    }

    public void OnProcessPacket(NodePacket packet)
    {
        if (packet.Header.IsReply == 1)
        {
            _requestCache.TryReply(packet.Header.RequestKey, packet);
        }
        else
        {
            _handler.ProcessPacket(packet);
        }
    }

    public void OnSendFailed(int requestKey, Exception ex) =>
        _requestCache.TryFail(requestKey, ex);
}
