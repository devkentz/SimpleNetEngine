using System.Diagnostics;
using NetMQ;
using SimpleNetEngine.Game.Actor;
using SimpleNetEngine.Game.Core;

namespace SimpleNetEngine.Game.Middleware;

/// <summary>
/// Zero-Copy 응답 전송 델리게이트
/// (gatewayNodeId, sessionId, response, requestId) => void
/// GameSessionChannelListener.SendResponse가 Msg에 직접 직렬화
/// </summary>
public delegate void SendResponseDelegate(long gatewayNodeId, long sessionId, Response response, ushort requestId, ushort sequenceId);

/// <summary>
/// 패킷 처리 Middleware 인터페이스
/// ASP.NET Core Middleware Pattern 적용
/// </summary>
public interface IPacketMiddleware
{
    /// <summary>
    /// Middleware 실행
    /// </summary>
    /// <param name="context">패킷 처리 컨텍스트</param>
    /// <param name="next">다음 Middleware 호출 delegate</param>
    Task InvokeAsync(PacketContext context, Func<Task> next);
}

/// <summary>
/// 패킷 처리 컨텍스트
/// Request/Response 정보 및 공유 데이터 포함
/// </summary>
public class PacketContext : IDisposable
{
    private Msg _payloadMsg;

    /// <summary>
    /// Gateway NodeId
    /// </summary>
    public long GatewayNodeId { get; set; }

    /// <summary>
    /// Session ID (0 = 미고정)
    /// </summary>
    public long SessionId { get; set; }

    /// <summary>
    /// 클라이언트 요청 ID (RPC 응답 매핑용)
    /// </summary>
    public ushort RequestId { get; set; }

    /// <summary>
    /// 클라이언트 패킷 데이터 (Zero-Copy: Msg 버퍼 직접 참조)
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; set; }

    /// <summary>
    /// Msg 소유권을 PacketContext로 이전 (Zero-Copy)
    /// Move 후 원본 Msg는 비초기화 상태가 됨
    /// </summary>
    /// <param name="msg">소유권을 이전할 Msg (Move 후 비초기화)</param>
    /// <param name="payloadOffset">Payload 시작 오프셋 (GSCHeader 크기)</param>
    public void TransferPayload(ref Msg msg, int payloadOffset)
    {
        _payloadMsg = new Msg();
        _payloadMsg.Move(ref msg);
        Payload = _payloadMsg.SliceAsMemory()[payloadOffset..];
    }

    /// <summary>
    /// 응답 데이터 (Zero-Copy: IMessage 래퍼)
    /// </summary>
    public Response? Response { get; set; }

    /// <summary>
    /// 처리 중 발생한 예외
    /// </summary>
    public Exception? Exception { get; set; }

    /// <summary>
    /// Middleware 간 데이터 공유용 Dictionary
    /// </summary>
    public Dictionary<string, object> Items { get; } = new();

    /// <summary>
    /// 처리 시작 시각 (성능 측정용)
    /// </summary>
    public DateTimeOffset StartTime { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 처리 완료 여부
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// Zero-Copy 응답 전송 콜백
    /// </summary>
    public SendResponseDelegate? SendResponse { get; set; }

    /// <summary>
    /// UserId (인증 후 설정됨)
    /// </summary>
    public long? UserId { get; set; }

    /// <summary>
    /// Opcode (패킷 파싱 후 설정됨)
    /// </summary>
    public int Opcode { get; set; }

    /// <summary>
    /// 분산 트레이싱 컨텍스트 (NetMQ Poller → Actor 스레드 전파용)
    /// GameSessionChannelListener에서 설정, SessionActor에서 parent로 사용
    /// </summary>
    public ActivityContext? ParentActivityContext { get; set; }

    public ushort SequenceId { get; set; }

    /// <summary>
    /// 현재 요청을 처리 중인 Actor (SessionActor에서 설정)
    /// SendNtf에서 SequenceId 자동 증가 및 라우팅 정보 참조에 사용
    /// </summary>
    public ISessionActor? Actor { get; set; }

    public ushort GetNextSequenceId()
    {
        return (ushort) (SequenceId + 1);
    }

    /// <summary>
    /// Notification 전송 (Server → Client 단방향)
    /// SequenceId를 Actor에서 자동 증가, RequestId=0으로 전송
    /// </summary>
    /// <param name="response">전송할 Notification 응답</param>
    /// <exception cref="InvalidOperationException">Actor 또는 SendResponse가 설정되지 않은 경우</exception>
    public void SendNtf(Response response)
    {
        if (Actor == null)
            throw new InvalidOperationException("Actor is not set on PacketContext. SendNtf requires an active Actor.");
        if (SendResponse == null)
            throw new InvalidOperationException("SendResponse callback is not set on PacketContext.");

        SendResponse(
            GatewayNodeId,
            SessionId,
            response,
            requestId: 0,
            sequenceId: Actor.NextSequenceId());
    }

    public void Dispose()
    {
        if (_payloadMsg.IsInitialised)
            _payloadMsg.Close();
    }
}

/// <summary>
/// PacketContext Scoped DI Holder
/// ASP.NET Core의 IHttpContextAccessor와 동일한 패턴:
/// SessionActor.ProcessMessageAsync에서 Scope 생성 후 값을 설정하면,
/// 동일 Scope 내 Controller에서 PacketContext를 직접 주입받을 수 있다.
/// </summary>
public class PacketContextHolder
{
    public PacketContext? Context { get; set; }
}
