namespace SimpleNetEngine.Game.Actor;

/// <summary>
/// Actor의 생명주기 및 인가 필터링에 사용되는 상태 값
/// </summary>
public enum ActorState
{
    /// <summary>
    /// Gateway로부터 접속 통보를 받아 메모리에 Session 채널만 생성된 익명 상태
    /// </summary>
    Created = 0,

    /// <summary>
    /// 로그인/인증을 진행 중인 상태 (중복 로그인 방지용 임시 State)
    /// </summary>
    Authenticating = 1,

    /// <summary>
    /// 인증이 완료되고 UserId가 바인딩된 정상 접근 가능 상태
    /// </summary>
    Active = 2,

    /// <summary>
    /// 통신 단절(네트워크 Disconnected) 상태
    /// </summary>
    Disconnected = 3,

    /// <summary>
    /// 자원이 회수되고 메모리에서 파기될 예정인 상태
    /// </summary>
    Disposed = 4
}
