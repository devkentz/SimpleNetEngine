using SimpleNetEngine.Node.Config;
using SimpleNetEngine.Protocol.Utils;
using Internal.Protocol;

namespace SimpleNetEngine.Game.Options;

/// <summary>
/// GameServer 설정 옵션 (라이브러리용)
/// </summary>
public class GameOptions
{
    /// <summary>
    /// </summary>
    public Guid NodeGuid { get; set; } = Guid.NewGuid();

    /// <summary>
    /// GameSessionChannel 수신 포트 (Gateway → GameServer, 클라이언트 패킷 수신)
    /// </summary>
    public int GameSessionChannelPort { get; set; } = 9001;

    /// <summary>
    /// GameSessionChannel 송신 포트 (GameServer → Gateway, 서버 응답 전송)
    /// 0이면 GameSessionChannelPort + 1 자동 할당
    /// </summary>
    public int GameSessionChannelSendPort { get; set; } = 0;

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
    /// 클라이언트 Idle Ping 전송 간격 (Handshake 응답으로 전달).
    /// 클라이언트는 이 간격마다 PingReq를 자동 전송하여 서버 Inactivity 타임아웃 방지.
    /// TimeSpan.Zero이면 클라이언트 Ping 비활성화.
    /// </summary>
    public TimeSpan ClientPingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 암호화 활성화 여부 (Handshake 응답으로 전달).
    /// true면 ECDH 키 교환 후 모든 패킷 AES-256-GCM 암호화 필수.
    /// </summary>
    public bool EncryptionEnabled { get; set; } = true;

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
            Host = NetworkHelper.GetLocalIpAddress(),
            RedisConnectionString = RedisConnectionString
        };

        // GameSessionChannel 포트를 메타데이터에 추가 (Gateway가 연결할 때 사용)
        nodeConfig.Metadata[global::SimpleNetEngine.Node.Core.NodeMetadataKeys.GameSessionChannelPort] =
            GameSessionChannelPort.ToString();
        nodeConfig.Metadata[global::SimpleNetEngine.Node.Core.NodeMetadataKeys.GameSessionChannelSendPort] =
            (GameSessionChannelSendPort > 0 ? GameSessionChannelSendPort : GameSessionChannelPort + 1).ToString();

        return nodeConfig;
    }
}
