using Google.Protobuf;
using Internal.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleNetEngine.Infrastructure;
using SimpleNetEngine.Protocol.Utils;
using SimpleNetEngine.Node.Cluster;
using SimpleNetEngine.Node.Config;
using SimpleNetEngine.Node.Network;

namespace SimpleNetEngine.Node.Core;

/// <summary>
/// 클러스터의 한 노드로서의 생명주기, 클러스터 참여, 다른 노드와의 통신을 관리하는 핵심 서비스입니다.
/// </summary>
public class NodeService : IDisposable
{
    private readonly ILogger<NodeService> _logger;
    private readonly INodeManager _nodeManager;
    private readonly NodeConfig _config;
    private readonly IClusterRegistry _clusterRegistry;
    private readonly NodeEventHandler _nodeEventHandler;
    private readonly NodeCommunicator _nodeCommunicator;
    private readonly IApplicationStopper _applicationStopper;

    private readonly byte[] _identity;
    private readonly long _nodeId;
    private ServerInfo _serverInfo = null!;

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private bool _isDispose;

    public NodeService(
        ILogger<NodeService> logger, INodeManager manager,
        IOptions<NodeConfig> config,
        IClusterRegistry clusterRegistry,
        NodeEventHandler nodeEventHandler,
        NodeCommunicator nodeCommunicator,
        NodePacketRouter nodePacketRouter,
        IApplicationStopper applicationStopper)
    {
        _logger = logger;
        _nodeManager = manager;
        _config = config.Value;
        _clusterRegistry = clusterRegistry;
        _nodeEventHandler = nodeEventHandler;
        _applicationStopper = applicationStopper;

        // NodePacketRouter is injected to force its instantiation.
        // Its constructor subscribes to NodeCommunicator.OnProcessPacket,
        // which is required for Service Mesh RPC routing to work.
        _ = nodePacketRouter;

        _nodeCommunicator = nodeCommunicator;
        _nodeCommunicator.OnJoinNode += OnJoinNode;

        _identity = _config.NodeGuid.ToByteArray();
        _nodeId = _config.NodeId;
    }

    private void OnJoinNode(NodeHandShakeReq packet)
    {
        var newNode = new RemoteNode(packet.Info);
        if (_nodeManager.TryAdd(packet.Info.RemoteId, newNode))
        {
            _logger.LogDebug("New node joined and added to manager. RemoteId: {RemoteId}, Address: {Address}",
                packet.Info.RemoteId, packet.Info.Address);

            _nodeEventHandler.OnJoinNode(newNode);
        }
    }

    private void OnLeaveNode(RemoteNode remoteNode)
    {
        // Connect()를 호출한 쪽(큰 ID)만 Disconnect() 가능.
        // 상대가 Connect한 경우 Bind된 Router가 자동 정리하므로 호출 불필요.
        if (_nodeId > remoteNode.Identity)
        {
            try
            {
                _nodeCommunicator.Disconnect(remoteNode.Address);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to disconnect remote node {RemoteId}", remoteNode.Identity);
            }
        }

        _nodeEventHandler.OnLeaveNode(remoteNode);
    }

    public long NodeId => _nodeId;

    public async Task StartAsync()
    {
        try
        {
            var actualPort = _nodeCommunicator.Start(_identity, _config.Port);
            var address = string.IsNullOrEmpty(_config.Host)
                ? $"tcp://{NetworkHelper.GetLocalIpAddress()}:{actualPort}"
                : $"tcp://{_config.Host}:{actualPort}";

            _serverInfo = new ServerInfo
            {
                IdentityBytes = ByteString.CopyFrom(_identity),
                RemoteId = _nodeId,
                Type = _config.ServerType,
                Name = _config.Name,
                Address = address,
            };

            // NodeConfig.Metadata → ServerInfo.Metadata 복사 (GameSessionChannelPort 등)
            foreach (var kvp in _config.Metadata)
            {
                _serverInfo.Metadata[kvp.Key] = kvp.Value;
            }

            _logger.LogDebug("My ServerInfo: {ServerInfo}", _serverInfo.ToString());

            await JoinClusterAsync();

            _ = Task.Factory.StartNew(() => HeartBeatLoopAsync(_cancellationTokenSource.Token), TaskCreationOptions.LongRunning).Unwrap();
            _ = Task.Factory.StartNew(() => CheckClusterStateLoopAsync(_cancellationTokenSource.Token), TaskCreationOptions.LongRunning).Unwrap();

            _logger.LogInformation("NodeService started successfully on port {Port}", actualPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start NodeService. Cleaning up...");
            Dispose(); // 시작 실패 시 리소스 정리
            throw;
        }
    }

    private async Task JoinClusterAsync()
    {
        var otherNodes = await _clusterRegistry.GetOtherLiveNodesAsync(_serverInfo.RemoteId);

        // 기존 노드들에게 연결
        var req = new NodeHandShakeReq {Info = _serverInfo};
        foreach (var info in otherNodes)
        {
            try
            {
                var remote = new RemoteNode(info);
                await _nodeCommunicator.ConnectAsync(remote, req);
                if (_nodeManager.TryAdd(info.RemoteId, remote))
                {
                    _nodeEventHandler.OnJoinNode(remote);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to connect to existing remote node: {NodeInfo}", info);
            }
        }

        await _clusterRegistry.RegisterSelfAsync(_serverInfo, TimeSpan.FromSeconds(_config.HeartBeatTtlSeconds));
    }

    private async Task HeartBeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.HeartBeatIntervalSeconds), ct);
                
                await _clusterRegistry.UpdateHeartbeatAsync(_nodeId, TimeSpan.FromSeconds(_config.HeartBeatTtlSeconds));
                _logger.LogDebug("Heartbeat updated for {NodeId}", _nodeId);
                
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Heartbeat loop. It will retry.");
                await Task.Delay(TimeSpan.FromSeconds(1), ct); // 에러 발생 시 잠시 후 재시도
            }
        }
    }

    private async Task CheckClusterStateLoopAsync(CancellationToken ct)
    {
        var lastKnownNodeIds = _nodeManager.GetAllNodes().Select(n => n.Identity).ToHashSet();
        lastKnownNodeIds.Add(_nodeId);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.HeartBeatIntervalSeconds), ct);

                // 클러스터의 현재 노드 ID 목록 조회를 IClusterRegistry에 위임
                var currentLiveSet = await _clusterRegistry.GetLiveNodeIdsAsync();

                if (!currentLiveSet.Contains(_nodeId))
                {
                    _logger.LogCritical("CRITICAL: This node {NodeId} is no longer in the cluster registry (Heartbeat timeout?). Initiating graceful shutdown.", _nodeId);
                    _applicationStopper.StopApplication();
                    return;
                }

                var deadNodeIds = lastKnownNodeIds.Except(currentLiveSet);
                foreach (var deadId in deadNodeIds)
                {
                    if (_nodeManager.TryRemove(deadId, out var deadNode))
                    {
                        _logger.LogDebug("Node detected dead: {DeadNodeId}, removing from manager.", deadId);
                        deadNode.ConnectionClosed();

                        OnLeaveNode(deadNode);
                    }
                }

                var newNodeIds = currentLiveSet.Except(lastKnownNodeIds);
                foreach (var newId in newNodeIds)
                {
                    if (newId == _nodeId)
                        continue;

                    if (_nodeId > newId)
                    {
                        _logger.LogDebug("New node detected {_nodeId} : {NewNodeId}. Initiating connection.", _nodeId, newId);
                        await ConnectToNodeAsync(newId);
                    }
                    else
                    {
                        _logger.LogDebug("New node detected {_nodeId} : {NewNodeId}. Waiting for it to initiate connection.", _nodeId, newId);
                    }
                }

                lastKnownNodeIds = currentLiveSet;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cluster state check loop. It will retry.");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    private async Task ConnectToNodeAsync(long newNodeId)
    {
        // 이미 연결된 노드라면 중복 연결을 시도하지 않음
        if (_nodeManager.FindNode(newNodeId) != null)
            return;

        try
        {
            // 새 노드 정보 조회를 IClusterRegistry에 위임
            var info = await _clusterRegistry.GetNodeInfoAsync(newNodeId);
            if (info == null)
            {
                _logger.LogWarning("Could not find ServerInfo for new node {NodeId}, it may have disappeared.", newNodeId);
                return;
            }

            var remoteNode = new RemoteNode(info);
            var req = new NodeHandShakeReq {Info = _serverInfo};
            await _nodeCommunicator.ConnectAsync(remoteNode, req);

            if (!_nodeManager.TryAdd(newNodeId, remoteNode))
            {
                _logger.LogDebug("New node {NodeId} was already present in NodeManager.", newNodeId);
            }
            else
            {
                _logger.LogDebug("Successfully connected to new node {NodeId}.", newNodeId);
                _nodeEventHandler.OnJoinNode(remoteNode);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to connect to new node {NodeId}.", newNodeId);
            _nodeManager.TryRemove(newNodeId, out _);
        }
    }

    public void Dispose()
    {
        if (_isDispose) return;
        _isDispose = true;

        _logger.LogInformation("Disposing NodeService...");
        _cancellationTokenSource.Cancel();

        try
        {
            // 노드 등록 해제를 IClusterRegistry에 위임
            _clusterRegistry.UnregisterSelfAsync(_nodeId).Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unregister node from cluster during shutdown.");
        }

        _nodeCommunicator.Dispose();
        _cancellationTokenSource.Dispose();
        _logger.LogInformation("NodeService disposed.");
    }

    public void Stop() => Dispose();
}