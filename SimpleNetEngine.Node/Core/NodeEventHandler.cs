using Microsoft.Extensions.Logging;
using SimpleNetEngine.Node.Network;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Node.Core;

public abstract class NodeEventHandler
{
    protected readonly ILogger _logger;

    public NodeEventHandler(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 패킷 수신 시 호출 — 각 서브클래스가 동시성 모델에 맞게 구현
    /// </summary>
    public abstract void ProcessPacket(NodePacket packet);

    public virtual void OnLeaveNode(RemoteNode remoteNode) { }
    public virtual void OnJoinNode(RemoteNode remoteNode) { }
}
