using Internal.Protocol;
using SimpleNetEngine.Protocol.Utils;

namespace SimpleNetEngine.Node.Config;

/// <summary>
/// Stateless Service 설정 옵션.
/// NodeConfig를 직접 다루지 않고 필요한 프로퍼티만 노출.
/// </summary>
public class StatelessServiceOptions
{
    /// <summary>
    /// 서비스 이름 (Api 타입 노드의 그룹핑 키)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 노드 고유 식별자 (XxHash64로 NodeId 생성에 사용)
    /// </summary>
    public Guid NodeGuid { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Service Mesh Bind 포트 (Node 간 RPC 통신용)
    /// </summary>
    public int ServiceMeshPort { get; set; }

    /// <summary>
    /// 포트 충돌 시 자동으로 다음 포트 시도 여부
    /// </summary>
    public bool AllowDynamicPort { get; set; } = true;

    /// <summary>
    /// Redis 연결 문자열
    /// </summary>
    public string RedisConnectionString { get; set; } = "localhost:6379";

    /// <summary>
    /// Node Config로 변환
    /// </summary>
    public NodeConfig ToNodeConfig()
    {
        return new NodeConfig
        {
            NodeGuid = NodeGuid,
            Port = ServiceMeshPort,
            ServerType = EServerType.Api,
            Name = Name,
            Host = AllowDynamicPort ? NetworkHelper.GetLocalIpAddress() : "",
            RedisConnectionString = RedisConnectionString,
        };
    }
}
