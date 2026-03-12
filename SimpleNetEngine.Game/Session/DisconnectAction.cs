namespace SimpleNetEngine.Game.Session;

/// <summary>
/// 연결 해제 시 Actor 처리 정책
/// ILoginHandler Hook에서 반환하여 라이브러리가 분기 처리
/// </summary>
public enum DisconnectAction
{
    /// <summary>
    /// 세션 복구 허용: Disconnected 전이 + Grace Period (재접속 대기)
    /// </summary>
    AllowSessionResume,

    /// <summary>
    /// 즉시 세션 종료: Actor 제거 + 정리 (재접속 불가)
    /// </summary>
    TerminateSession
}
