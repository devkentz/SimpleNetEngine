namespace GatewaySample.Config;

/// <summary>
/// appsettings.json 매핑용 Config 클래스
/// (Sample 프로젝트에만 존재)
/// </summary>
public class GatewayConfig
{
    public Guid NodeGuid { get; set; } = Guid.NewGuid();
    public string ClientHost { get; set; } = "0.0.0.0";
    public int TcpPort { get; set; }
    public bool AllowDynamicPort { get; set; } = false;
    public int GameSessionChannelPort { get; set; }
    public int ServiceMeshPort { get; set; }
    public string RedisConnectionString { get; set; } = "redis-dev.k8s.home:6379";

    /// <summary>
    /// ECDSA P-256 서명 개인키 PEM 파일 경로 (MITM 방지)
    /// 미설정 시 서명 없이 동작 (개발 모드)
    /// </summary>
    public string? SigningKeyPath { get; set; }
}
