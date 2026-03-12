using Internal.Protocol;

namespace SimpleNetEngine.Node.Cluster;

/// <summary>
/// 클러스터의 서비스 검색 및 레지스트리 역할을 정의하는 인터페이스입니다.
/// 노드 정보 등록, 검색, 상태 관리 등의 책임을 가집니다.
/// </summary>
public interface IClusterRegistry
{
    /// <summary>
    /// 자신을 등록함과 동시에 다른 노드 목록을 원자적(Atomic)으로 가져옵니다.
    /// Lua Script를 사용하여 동시성 문제를 해결합니다.
    /// </summary>
    Task<List<ServerInfo>> RegisterAndGetOtherNodesAsync(ServerInfo selfInfo, TimeSpan ttl);

    /// <summary>
    /// 자신을 제외한, 현재 활성화된 다른 모든 노드의 정보를 가져옵니다.
    /// </summary>
    Task<List<ServerInfo>> GetOtherLiveNodesAsync(long selfNodeId);
    
    /// <summary>
    /// Redis에 자신의 노드 정보를 등록(또는 업데이트)하고 TTL을 설정합니다.
    /// </summary>
    Task RegisterSelfAsync(ServerInfo selfInfo, TimeSpan ttl);

    /// <summary>
    /// 자신의 Heartbeat(TTL)을 갱신합니다.
    /// </summary>
    Task UpdateHeartbeatAsync(long selfNodeId, TimeSpan ttl);

    /// <summary>
    /// 현재 활성화된 모든 노드의 ID를 가져옵니다.
    /// </summary>
    Task<HashSet<long>> GetLiveNodeIdsAsync();

    /// <summary>
    /// 특정 노드의 상세 정보를 가져옵니다.
    /// </summary>
    Task<ServerInfo?> GetNodeInfoAsync(long nodeId);

    /// <summary>
    /// 클러스터에서 자신의 노드 정보를 제거합니다.
    /// </summary>
    Task UnregisterSelfAsync(long selfNodeId);
}
