using System;

namespace SimpleNetEngine.Client.Config;

/// <summary>
/// NetClient 설정
/// </summary>
public class NetClientConfig
{
    /// <summary>
    /// RPC 요청 타임아웃 (기본: 15초)
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 수신 메시지 큐 최대 크기 (기본: 1000)
    /// </summary>
    public int MaxQueueSize { get; init; } = 1000;

    /// <summary>
    /// TCP_NODELAY 옵션 (기본: true)
    /// </summary>
    public bool NoDelay { get; init; } = true;

    /// <summary>
    /// TCP Keep-Alive 옵션 (기본: true)
    /// </summary>
    public bool KeepAlive { get; init; } = true;

    /// <summary>
    /// 연결 타임아웃 (기본: 15초)
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Idle 상태에서 PingReq 자동 전송 간격.
    /// 이 시간 동안 패킷을 보내지 않으면 PingReq를 자동 전송하여 서버 Inactivity 타임아웃 방지.
    /// TimeSpan.Zero이면 자동 Ping 비활성화.
    /// </summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 서버 시간 동기화 요청 주기 (기본: 10초).
    /// 트래픽 유무와 관계없이 이 간격으로 TimeSyncReq를 강제 전송하여 RTT 및 서버 시간 오프셋을 측정.
    /// TimeSpan.Zero이면 시간 동기화 비활성화.
    /// </summary>
    public TimeSpan TimeSyncInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 서버 ECDSA 서명 검증용 공개키 PEM 파일 경로.
    /// null이면 서명 검증 없이 동작 (개발 모드, MITM 취약).
    /// 파일이 존재하지 않으면 경고 로그 후 서명 검증 없이 동작.
    /// </summary>
    public string? SigningPublicKeyPath { get; init; }
}
