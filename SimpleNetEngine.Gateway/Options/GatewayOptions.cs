using SimpleNetEngine.Node.Config;
using Internal.Protocol;

namespace SimpleNetEngine.Gateway.Options;

/// <summary>
/// Gateway 설정 옵션 (라이브러리용)
/// </summary>
public class GatewayOptions
{
    /// <summary>
    /// 클라이언트 TCP 호스트 (IP)
    /// </summary>
    public string ClientHost { get; set; } = "0.0.0.0";

    /// <summary>
    /// 클라이언트 TCP 포트
    /// </summary>
    public int TcpPort { get; set; } = 5000;

    /// <summary>
    /// 포트 충돌 시 자동으로 다음 포트 시도 여부
    /// - true: 테스트/개발 환경 (포트 충돌 시 자동 증가)
    /// - false: 프로덕션 환경 (고정 포트, 충돌 시 시작 실패)
    /// </summary>
    public bool AllowDynamicPort { get; set; } = false;

    /// <summary>
    /// 노드 고유 식별자 (XxHash64로 NodeId 생성에 사용)
    /// </summary>
    public Guid NodeGuid { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Service Mesh Bind 포트 (Node 간 RPC 통신용)
    /// </summary>
    public int ServiceMeshPort { get; set; } = 9201;

    /// <summary>
    /// Redis 연결 문자열
    /// </summary>
    public string RedisConnectionString { get; set; } = "localhost:6379";

    /// <summary>
    /// 압축 활성화 여부
    /// </summary>
    public bool EnableCompression { get; set; } = true;

    /// <summary>
    /// 압축 임계값 (바이트). 이 크기 미만의 패킷은 압축하지 않음
    /// </summary>
    public int CompressionThreshold { get; set; } = 128;

    /// <summary>
    /// 암호화 활성화 여부 (ECDH 키 교환 + AES-256-GCM)
    /// false: ECDH 키 교환 생략, 모든 패킷 평문 전송 (개발/테스트용)
    /// true: ECDH 키 교환 수행, 선택적 암호화 지원 (프로덕션)
    /// </summary>
    public bool EnableEncryption { get; set; } = true;

    /// <summary>
    /// ECDSA P-256 서명 개인키 PEM 파일 경로 (MITM 방지)
    /// 설정하지 않으면 서명 없이 동작 (개발 모드)
    /// EnableEncryption이 false이면 무시됨
    /// </summary>
    public string? SigningKeyPath { get; set; }

    /// <summary>
    /// Gateway Node ID (NodeGuid로부터 생성됨, 내부적으로 사용)
    /// </summary>
    internal long GatewayNodeId { get; set; }

    /// <summary>
    /// Node Config로 변환 (자체 NodeGuid/ServiceMeshPort 사용)
    /// </summary>
    public NodeConfig ToNodeConfig()
    {
        return new NodeConfig
        {
            NodeGuid = NodeGuid,
            Port = ServiceMeshPort,
            ServerType = EServerType.Gateway,
            Host = "127.0.0.1",
            RedisConnectionString = RedisConnectionString
        };
    }

}
