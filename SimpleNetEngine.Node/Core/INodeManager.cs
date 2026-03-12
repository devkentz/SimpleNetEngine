using System.Diagnostics.CodeAnalysis;
using SimpleNetEngine.Node.Models;
using SimpleNetEngine.Node.Network;

namespace SimpleNetEngine.Node.Core;

public interface INodeManager
{
    public RemoteNode? FindNode(long remoteId);
    public RemoteNode? RoundRobinByApiName(string apiName);
    bool TryAdd(long remoteId, RemoteNode node);
    bool TryRemove(long remoteId, [NotNullWhen(returnValue: true)] out RemoteNode? node);
    public byte[]? FindRemoteKey(long remoteId);
    ApiInfo? GetApiInfo(string apiName);
    IReadOnlyCollection<RemoteNode> GetAllNodes();
}