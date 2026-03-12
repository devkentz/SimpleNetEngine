using SimpleNetEngine.Game.Middleware;

namespace SimpleNetEngine.Game.Actor;

/// <summary>
/// Actor 메시지 인터페이스
/// Middleware Pipeline에서 Actor로 전달되는 메시지 단위
/// </summary>
public interface IActorMessage
{
    /// <summary>
    /// Gateway NodeId (응답 라우팅용)
    /// </summary>
    long GatewayNodeId { get; }

    /// <summary>
    /// 게임 세션 ID
    /// </summary>
    long SessionId { get; }

    /// <summary>
    /// Sequence ID (Idempotency)
    /// </summary>
    long SequenceId { get; }

    /// <summary>
    /// 클라이언트 요청 ID (RPC 응답 매핑용)
    /// </summary>
    ushort RequestId { get; }

    /// <summary>
    /// 클라이언트 원본 페이로드 (P2P 헤더 제외, Zero-Copy Msg 버퍼 참조)
    /// </summary>
    ReadOnlyMemory<byte> Payload { get; }

    /// <summary>
    /// Zero-Copy 응답 전송 콜백
    /// </summary>
    SendResponseDelegate? SendResponse { get; }
}

/// <summary>
/// PacketContext를 직접 래핑하는 Actor 메시지
/// 변환 없이 PacketContext를 그대로 Actor mailbox에 전달
/// </summary>
public sealed class PacketActorMessage(PacketContext context) : IActorMessage
{
    public PacketContext Context { get; } = context;

    public long GatewayNodeId => Context.GatewayNodeId;
    public long SessionId => Context.SessionId;
    public long SequenceId => Context.SequenceId;
    public ushort RequestId => Context.RequestId;
    public ReadOnlyMemory<byte> Payload => Context.Payload;
    public SendResponseDelegate? SendResponse => Context.SendResponse;
}

/// <summary>
/// Actor mailbox에서 임의의 비동기 콜백을 순차 실행하기 위한 메시지
/// 외부 스레드(NodeController, LoginController 등)에서 Actor 상태에 안전하게 접근할 때 사용
/// Scoped DI 컨테이너가 생성되어 콜백에 전달됨
/// </summary>
public sealed class CallbackActorMessage(Func<IServiceProvider, Task> callback) : IActorMessage
{
    public Func<IServiceProvider, Task> Callback { get; } = callback;
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long GatewayNodeId => 0;
    public long SessionId => 0;
    public long SequenceId => 0;
    public ushort RequestId => 0;
    public ReadOnlyMemory<byte> Payload => ReadOnlyMemory<byte>.Empty;
    public SendResponseDelegate? SendResponse => null;
}
