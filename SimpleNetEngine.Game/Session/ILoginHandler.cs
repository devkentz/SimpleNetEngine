using Game.Protocol;
using SimpleNetEngine.Game.Actor;

namespace SimpleNetEngine.Game.Session;

/// <summary>
/// 앱이 구현할 로그인 확장점 인터페이스
/// 라이브러리가 인증 플로우의 골격(Skeleton)을 소유하고,
/// 앱이 이 Hook으로 비즈니스 로직을 주입한다.
/// </summary>
public interface ILoginHandler
{
    /// <summary>
    /// credential → UserId 변환 (JWT, OAuth 등 게임별 인증)
    /// credential은 앱이 정의한 protobuf 메시지의 직렬화 바이트
    /// </summary>
    Task<LoginAuthResult> AuthenticateAsync(ReadOnlyMemory<byte> credential, ISessionActor actor);

    /// <summary>
    /// 로그인 성공 후 Actor 초기화 (DB 로드, 게임 상태 설정)
    /// 이 Hook 실행 후 라이브러리가 actor.Status = Active 전이
    /// </summary>
    Task OnLoginSuccessAsync(ISessionActor actor);

    /// <summary>
    /// 재접속 성공 시 (Disconnected → Active 복원)
    /// gapInfo를 확인하여 유실 패킷 대응 (상태 스냅샷 재전송, 클라이언트 재전송 요청 등)
    /// </summary>
    Task OnReconnectedAsync(ISessionActor actor, ReconnectGapInfo gapInfo);

    /// <summary>
    /// Kickout 수신 시 (중복 로그인에 의한 강제 퇴장)
    /// 게임 데이터 저장 후 DisconnectAction 반환:
    /// - TerminateSession: 즉시 Actor 제거 (재접속 불가)
    /// - AllowSessionResume: Disconnected 전이 + Grace Period (재접속 허용)
    /// </summary>
    Task<DisconnectAction> OnKickoutAsync(ISessionActor actor, EKickoutReason reason);

    /// <summary>
    /// 비활성 타임아웃 시 DisconnectAction 결정
    /// 기본값: AllowSessionResume (Disconnected + Grace Period)
    /// </summary>
    Task<DisconnectAction> OnInactivityTimeoutAsync(ISessionActor actor)
        => Task.FromResult(DisconnectAction.AllowSessionResume);

    /// <summary>
    /// 비의도적 연결 끊김 → 재접속 대기 진입 시
    /// 매칭 일시정지, 파티원 알림 등
    /// </summary>
    Task OnDisconnectedAsync(ISessionActor actor);

    /// <summary>
    /// 의도적 로그아웃 또는 Grace Period 만료
    /// 최종 데이터 저장
    /// </summary>
    Task OnLogoutAsync(ISessionActor actor);
}

/// <summary>
/// ILoginHandler.AuthenticateAsync 반환값
/// </summary>
public readonly record struct LoginAuthResult
{
    public bool IsSuccess { get; init; }
    public long UserId { get; init; }
    public int ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static LoginAuthResult Success(long userId) =>
        new() { IsSuccess = true, UserId = userId };

    public static LoginAuthResult Failure(int errorCode, string? message = null) =>
        new() { IsSuccess = false, ErrorCode = errorCode, ErrorMessage = message };
}

/// <summary>
/// 재접속 시 SequenceId gap 정보
/// 앱 개발자가 유실 패킷을 감지하고 대응할 수 있도록 제공
/// </summary>
/// <param name="ClientReportedLastServerSeqId">클라이언트가 마지막으로 수신한 서버 SequenceId</param>
/// <param name="ServerCurrentSeqId">서버의 현재 SequenceId (Actor가 발급한 마지막 값)</param>
/// <param name="ClientReportedLastClientSeqId">클라이언트가 마지막으로 송신한 자신의 SequenceId</param>
/// <param name="ServerLastValidatedClientSeqId">서버가 마지막으로 검증한 클라이언트 SequenceId</param>
public readonly record struct ReconnectGapInfo(
    ushort ClientReportedLastServerSeqId,
    ushort ServerCurrentSeqId,
    ushort ClientReportedLastClientSeqId,
    ushort ServerLastValidatedClientSeqId)
{
    /// <summary>
    /// 서버→클라이언트 방향 유실 패킷 존재 여부
    /// true면 서버가 보냈지만 클라이언트가 못 받은 패킷이 있음 → 상태 스냅샷 재전송 고려
    /// </summary>
    public bool HasServerToClientGap => ServerCurrentSeqId != ClientReportedLastServerSeqId;

    /// <summary>
    /// 클라이언트→서버 방향 유실 패킷 존재 여부
    /// true면 클라이언트가 보냈지만 서버가 못 받은 패킷이 있음 → 클라이언트 재전송 또는 무시
    /// </summary>
    public bool HasClientToServerGap => ClientReportedLastClientSeqId != ServerLastValidatedClientSeqId;
}

