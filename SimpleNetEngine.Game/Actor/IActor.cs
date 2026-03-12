namespace SimpleNetEngine.Game.Actor;

/// <summary>
/// 게임 세션 Actor 인터페이스 (클라이언트 유저와 1:1 대응)
/// TcpServer의 IActor 패턴을 GameServer P2P 환경에 맞게 확장
///
/// TcpServer IActor: ActorId + Push
/// GameServer ISessionActor: + UserId, GatewayNodeId, UpdateRouting, State
/// </summary>
public interface ISessionActor : IDisposable
{
    /// <summary>
    /// Actor 고유 식별자 (SessionId와 동일)
    /// Reconnect 시 ActorManager.RekeyActor()를 통해 변경될 수 있음
    /// </summary>
    long ActorId { get; set; }

    /// <summary>
    /// 연결된 유저 ID
    /// </summary>
    long UserId { get; set; }

    /// <summary>
    /// 현재 Gateway NodeId (응답 라우팅용)
    /// </summary>
    long GatewayNodeId { get; }

    /// <summary>
    /// Actor의 비즈니스 생명주기 상태 (패킷 접근 제어용)
    /// </summary>
    ActorState Status { get; set; }

    /// <summary>
    /// 패킷 순서 보장을 위한 시퀀스 번호 (원자적 증가)
    /// </summary>
    long SequenceId { get; }

    /// <summary>
    /// 마지막 클라이언트 패킷 수신 시각 (Stopwatch.GetTimestamp)
    /// InactivityScanner가 idle Actor를 감지하는 데 사용
    /// </summary>
    long LastActivityTicks { get; }

    /// <summary>
    /// 클라이언트 Activity 갱신 (패킷 수신 시 호출)
    /// </summary>
    void TouchActivity();

    /// <summary>
    /// 재접속 토큰 (익명 상태에서 재접속 시 동일 세션 복구용)
    /// 로그인 완료 시 무효화되며, 새로운 키로 갱신됨
    /// </summary>
    Guid ReconnectKey { get; }

    /// <summary>
    /// Actor별 상태 저장소 (게임 로직에서 사용)
    /// </summary>
    Dictionary<string, object> State { get; }

    /// <summary>
    /// 메시지를 Actor mailbox에 추가 (non-blocking)
    /// </summary>
    void Push(IActorMessage message);

    /// <summary>
    /// 재접속 시 라우팅 정보 갱신
    /// </summary>
    void UpdateRouting(long gatewayNodeId);

    /// <summary>
    /// 다음 시퀀스 번호 발급 (원자적 증가)
    /// </summary>
    long NextSequenceId();

    /// <summary>
    /// 새로운 Reconnect Key 발급 (로그인 완료 시 호출)
    /// </summary>
    Guid RegenerateReconnectKey();

    /// <summary>
    /// Disconnected 상태 진입 시각 (Stopwatch.GetTimestamp 기반).
    /// InactivityScanner가 Grace Period 만료를 판단하는 데 사용.
    /// 0이면 Disconnected 상태가 아님.
    /// </summary>
    long DisconnectedTicks { get; }

    /// <summary>
    /// Disconnected 상태 진입 시각 기록
    /// </summary>
    void MarkDisconnected();

    /// <summary>
    /// Disconnected 타임스탬프 초기화 (재접속 성공 시 호출)
    /// </summary>
    void ClearDisconnected();

    /// <summary>
    /// Actor mailbox에 비동기 콜백을 push하고 완료를 대기.
    /// 외부 스레드에서 Actor 상태에 thread-safe하게 접근할 때 사용.
    /// Scoped DI 컨테이너가 생성되어 콜백에 전달됨.
    /// (예: KickoutController, LoginController의 Same-Node 중복 처리)
    /// </summary>
    Task ExecuteAsync(Func<IServiceProvider, Task> action);
}
