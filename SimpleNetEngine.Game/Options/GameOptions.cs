using SimpleNetEngine.Node.Config;
using Internal.Protocol;

namespace SimpleNetEngine.Game.Options;

/// <summary>
/// GameServer 설정 옵션 (라이브러리용)
/// </summary>
public class GameOptions
{
    /// <summary>
    /// 노드 고유 식별자 (XxHash64로 NodeId 생성에 사용)
    /// </summary>
    public Guid NodeGuid { get; set; } = Guid.NewGuid();

    /// <summary>
    /// GameSessionChannel Bind 포트 (Gateway 연결용)
    /// </summary>
    public int GameSessionChannelPort { get; set; } = 9001;

    /// <summary>
    /// 포트 충돌 시 자동으로 다음 포트 시도 여부
    /// - true: 테스트/개발 환경 (포트 충돌 시 자동 증가)
    /// - false: 프로덕션 환경 (고정 포트, 충돌 시 시작 실패)
    /// </summary>
    public bool AllowDynamicPort { get; set; } = false;

    /// <summary>
    /// Service Mesh Bind 포트 (Node 간 RPC 통신용)
    /// </summary>
    public int ServiceMeshPort { get; set; } = 9101;

    /// <summary>
    /// Redis 연결 문자열 (SSOT)
    /// </summary>
    public string RedisConnectionString { get; set; } = "localhost:6379";

    /// <summary>
    /// 클라이언트 Inactivity 타임아웃.
    /// 이 시간 동안 아무 패킷도 수신하지 못하면 Disconnect 처리 (재접속 가능 상태).
    /// TimeSpan.Zero이면 Inactivity 감지 비활성화.
    /// </summary>
    public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Disconnect 후 재접속 대기 시간 (Grace Period)
    /// 이 시간 내에 ReconnectReq가 없으면 OnLogoutAsync → Actor 제거 → Redis 삭제
    /// </summary>
    public TimeSpan ReconnectGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// GameServer Node ID (NodeGuid로부터 생성됨, 내부적으로 사용)
    /// </summary>
    internal long GameNodeId { get; set; }

    /// <summary>
    /// Node Config로 변환 (자체 NodeGuid/ServiceMeshPort 사용)
    /// </summary>
    public NodeConfig ToNodeConfig()
    {
        var nodeConfig = new NodeConfig
        {
            NodeGuid = NodeGuid,
            Port = ServiceMeshPort,
            ServerType = EServerType.Game,
            Host = "127.0.0.1",
            RedisConnectionString = RedisConnectionString
        };

        // GameSessionChannel 포트를 메타데이터에 추가 (Gateway가 연결할 때 사용)
        nodeConfig.Metadata[global::SimpleNetEngine.Node.Core.NodeMetadataKeys.GameSessionChannelPort] =
            GameSessionChannelPort.ToString();

        return nodeConfig;
    }
}
