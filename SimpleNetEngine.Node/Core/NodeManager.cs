using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using SimpleNetEngine.Node.Models;
using SimpleNetEngine.Node.Network;

namespace SimpleNetEngine.Node.Core;

public class NodeManager : INodeManager
{
    private readonly ConcurrentDictionary<long, RemoteNode> _nodesById = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, RemoteNode>> _nodesByApiName = new();
    private readonly ConcurrentDictionary<string, AtomicCounter> _roundRobinCounters = new();
    private readonly ConcurrentDictionary<string, ApiInfo> _apiInfos = new();
    
    private class AtomicCounter
    {
        private int _value;

        public int GetAndIncrement()
        {
            int initial, computed;
            do
            {
                initial = _value;
                computed = (initial == int.MaxValue) ? 0 : initial + 1;
            } while (Interlocked.CompareExchange(ref _value, computed, initial) != initial);

            return initial;
        }
    }


    public RemoteNode? FindNode(long remoteId)
    {
        return _nodesById.GetValueOrDefault(remoteId);
    }

    public byte[]? FindRemoteKey(long remoteId)
    {
        return _nodesById.GetValueOrDefault(remoteId)?.IdentityBytes;
    }

    public ApiInfo? GetApiInfo(string apiName)
    {
        return _apiInfos.GetValueOrDefault(apiName);
    }

    public IReadOnlyCollection<RemoteNode> GetAllNodes()
    {
        return  (IReadOnlyCollection<RemoteNode>) _nodesById.Values;
    }
 
    public RemoteNode? RoundRobinByApiName(string apiName)
    {
        if (!_nodesByApiName.TryGetValue(apiName, out var group))
            return null;

        var nodes = group.Values.ToArray();
        if (nodes.Length == 0)
            return null;

        var counter = _roundRobinCounters.GetOrAdd(apiName, _ => new AtomicCounter());
        int index = counter.GetAndIncrement() % nodes.Length;
        return nodes[index];
    }

    public bool TryAdd(long remoteId, RemoteNode node)
    {
        if (!_nodesById.TryAdd(remoteId, node))
            return false;
        
        Debug.Assert(node.ServerTypeString != "Unknown");

        var group = _nodesByApiName.GetOrAdd(node.ServerTypeString, _ => new ConcurrentDictionary<long, RemoteNode>());
        
        Debug.Assert(!group.ContainsKey(remoteId));

        group.TryAdd(remoteId, node);
        
        if(!_apiInfos.ContainsKey(node.ServerTypeString))
        {
           _apiInfos.TryAdd(node.ServerTypeString, new ApiInfo
           {
               ApiName = node.ServerTypeString,
               ServerType = node.ServerType
           });             
        }
        
        return true;
    }

    public bool TryRemove(long remoteId, [NotNullWhen(returnValue:true)] out RemoteNode? node)
    {
        if (!_nodesById.TryRemove(remoteId, out node))
            return false;

        if (_nodesByApiName.TryGetValue(node.ServerTypeString, out var group)) 
            group.TryRemove(remoteId, out _);

        return true;
    }
}