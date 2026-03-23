namespace SimpleNetEngine.Protocol.Packets;

/// <summary>
/// 통합 에러코드 (5자리 ABCCC 포맷)
///
/// A = 발생 계층: 1=Gateway, 2=GameServer, 3=StatelessService
/// B = 카테고리:  0=Connection, 1=Session, 2=Actor, 3=Storage, 4=Protocol, 9=Internal
/// CCC = 상세코드: 001~999
///
/// EndPointHeader.ErrorCode(short)로 전달됨 (max 32767 → Stateless 3xxxx는 B≤2까지만 사용 가능)
/// Result&lt;T&gt; 패턴에서 타입 안전한 에러 분류로도 사용
/// </summary>
public enum ErrorCode : short
{
    /// <summary>에러 없음 (성공 상태)</summary>
    None = 0,

    // ─────────────────────────────────────────────
    // 1xxxx: Gateway 발생
    // ─────────────────────────────────────────────

    // 10xxx: Gateway + Connection
    /// <summary>GameServer 종료로 인한 연결 해제</summary>
    GatewayGameServerShutdown = 10001,
    /// <summary>GameServer 전송 실패 (RouterMandatory — identity 미도달)</summary>
    GatewayGameServerUnreachable = 10002,
    /// <summary>클라이언트 응답 전송 실패 (TCP 소켓 쓰기 불가)</summary>
    GatewayClientSendFailed = 10003,

    // 11xxx: Gateway + Session
    /// <summary>Gateway 레벨 세션 만료</summary>
    GatewaySessionExpired = 11001,

    // 14xxx: Gateway + Protocol
    /// <summary>잘못된 패킷 크기/형식 (I/O 레벨 검증 실패)</summary>
    GatewayInvalidPacket = 14001,
    /// <summary>복호화 또는 암호화 처리 실패</summary>
    GatewayEncryptionError = 14002,
    /// <summary>Rate limit 초과</summary>
    GatewayRateLimitExceeded = 14003,

    // 12xxx: Gateway + Actor
    /// <summary>RequireActorState 검사 실패 (Gateway 경유)</summary>
    GatewayInvalidActorState = 12001,

    // ─────────────────────────────────────────────
    // 2xxxx: GameServer 발생
    // ─────────────────────────────────────────────

    // 20xxx: GameServer + Connection
    /// <summary>Gateway 연결 실패</summary>
    GameConnectionFailed = 20001,
    /// <summary>패킷 전송 실패</summary>
    GamePacketSendFailed = 20002,
    /// <summary>Service Mesh 통신 실패</summary>
    GameServiceMeshFailed = 20003,

    // 21xxx: GameServer + Session
    /// <summary>세션을 찾을 수 없음</summary>
    GameSessionNotFound = 21001,
    /// <summary>세션이 만료됨</summary>
    GameSessionExpired = 21002,
    /// <summary>세션 검증 실패</summary>
    GameSessionValidationFailed = 21003,
    /// <summary>중복 로그인</summary>
    GameDuplicateLogin = 21004,

    // 22xxx: GameServer + Actor
    /// <summary>Actor를 찾을 수 없음</summary>
    GameActorNotFound = 22001,
    /// <summary>Actor 추가 실패 (중복 등)</summary>
    GameActorAddFailed = 22002,
    /// <summary>Actor 상태가 유효하지 않음</summary>
    GameActorInvalidState = 22003,

    // 23xxx: GameServer + Storage
    /// <summary>Redis 연결 실패</summary>
    GameStorageConnectionFailed = 23001,
    /// <summary>Redis 읽기/쓰기 실패</summary>
    GameStorageOperationFailed = 23002,
    /// <summary>데이터 직렬화 실패</summary>
    GameSerializationFailed = 23003,

    // 24xxx: GameServer + Protocol
    /// <summary>잘못된 요청 형식</summary>
    GameInvalidRequest = 24001,
    /// <summary>필수 파라미터 누락</summary>
    GameMissingParameter = 24002,
    /// <summary>권한 없음</summary>
    GameUnauthorized = 24003,
    /// <summary>중복된 SequenceId (Idempotency 위반)</summary>
    GameDuplicateSequenceId = 24004,
    /// <summary>패킷 파싱 실패</summary>
    GamePacketParsingFailed = 24005,

    // 29xxx: GameServer + Internal
    /// <summary>내부 서버 오류</summary>
    GameInternalError = 29001,
    /// <summary>알 수 없는 오류</summary>
    GameUnknown = 29999,

    // ─────────────────────────────────────────────
    // 3xxxx: Stateless Service 발생 (향후 확장)
    // ─────────────────────────────────────────────

    // 30xxx: Stateless + Connection/Internal (short 범위 제약으로 B=9 사용 불가)
    // 31xxx: Stateless + Session
    // 32xxx: Stateless + Actor
    /// <summary>Stateless Service 내부 오류</summary>
    StatelessInternalError = 30001,
}
