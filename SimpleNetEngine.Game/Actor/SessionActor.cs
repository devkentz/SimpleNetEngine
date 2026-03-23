using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Game.Middleware;
using SimpleNetEngine.Infrastructure.Telemetry;
using SimpleNetEngine.Node.Core;
using SimpleNetEngine.Protocol.Packets;
using SimpleNetEngine.ProtoGenerator;

namespace SimpleNetEngine.Game.Actor;

/// <summary>
/// 세션별 Actor
/// TcpServer의 Actor 패턴을 GameServer GameSessionChannel 환경에 적응
///
/// 핵심 특성:
/// - QueuedResponseWriter mailbox로 메시지 순차 처리 (동시성 제어)
/// - 각 메시지 처리 시 Scoped DI 컨테이너 생성
/// - 미들웨어 파이프라인을 Actor 내부에서 실행 (정확한 처리 시간 측정)
/// - IMessageDispatcher를 통한 비즈니스 로직 위임
/// - 응답은 GameSessionChannel SendResponse 콜백으로 전송
/// </summary>
public class SessionActor : ISessionActor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMessageDispatcher _dispatcher;
    private readonly MiddlewarePipeline _pipeline;
    private readonly ILogger _logger;
    private readonly QueuedResponseWriter<IActorMessage> _mailbox;
    private int _sequenceId;
    private ushort _lastClientSequenceId;
    private bool _seqInitialized;
    private long _lastActivityTicks;
    private long _disconnectedTicks;
    private Guid _reconnectKey;

    public long ActorId { get; set; }
    public long UserId { get; set; }
    public long GatewayNodeId { get; private set; }
    public ActorState Status { get; set; }
    public Dictionary<string, object> State { get; } = [];
    public int SequenceId => _sequenceId;
    public ushort LastClientSequenceId => _lastClientSequenceId;
    public long LastActivityTicks => Volatile.Read(ref _lastActivityTicks);
    public long DisconnectedTicks => Volatile.Read(ref _disconnectedTicks);
    public Guid ReconnectKey => _reconnectKey;

    public SessionActor(
        long actorId,
        long userId,
        long gatewayNodeId,
        IServiceScopeFactory scopeFactory,
        IMessageDispatcher dispatcher,
        MiddlewarePipeline pipeline,
        ILogger logger)
    {
        ActorId = actorId;
        UserId = userId;
        GatewayNodeId = gatewayNodeId;
        Status = ActorState.Created;
        _scopeFactory = scopeFactory;
        _dispatcher = dispatcher;
        _pipeline = pipeline;
        _logger = logger;

        _sequenceId = Random.Shared.Next(1, ushort.MaxValue + 1);
        _lastActivityTicks = Stopwatch.GetTimestamp();
        _reconnectKey = Guid.NewGuid();

        _mailbox = new QueuedResponseWriter<IActorMessage>(ProcessMessageAsync, logger);
    }

    /// <summary>
    /// 메시지를 Actor mailbox에 추가 (non-blocking)
    /// </summary>
    public void Push(IActorMessage message)
    {
        _mailbox.Write(message);
    }

    /// <summary>
    /// 재접속 시 라우팅 정보 갱신 (SequenceId 리셋)
    /// 클라이언트가 새 인스턴스로 재접속하면 Handshake에서 새 SequenceId를 받으므로
    /// 서버도 검증 기준을 리셋하여 첫 패킷에서 다시 기준값을 설정
    /// </summary>
    public void UpdateRouting(long gatewayNodeId)
    {
        GatewayNodeId = gatewayNodeId;
        _seqInitialized = false;
        _lastClientSequenceId = 0;

        _logger.LogDebug(
            "Actor routing updated (seq reset): ActorId={ActorId}, Gateway={GatewayNodeId}",
            ActorId, gatewayNodeId);
    }

    /// <summary>
    /// 재접속 시 라우팅 정보 갱신 + 클라이언트 SequenceId 연속성 유지
    /// ReconnectReq에서 전달받은 lastClientSequenceId를 기준으로 검증 상태를 복원하여
    /// 다음 패킷부터 단조 증가 검증을 이어간다.
    /// </summary>
    public void UpdateRouting(long gatewayNodeId, ushort lastClientSequenceId)
    {
        GatewayNodeId = gatewayNodeId;
        _lastClientSequenceId = lastClientSequenceId;
        _seqInitialized = true;

        _logger.LogDebug(
            "Actor routing updated (seq continued): ActorId={ActorId}, Gateway={GatewayNodeId}, LastClientSeqId={SeqId}",
            ActorId, gatewayNodeId, lastClientSequenceId);
    }

    /// <summary>
    /// 다음 시퀀스 번호 발급 (원자적 증가, ushort 범위, 0 건너뛰기)
    /// </summary>
    public ushort NextSequenceId()
    {
        var next = Interlocked.Increment(ref _sequenceId);
        if ((ushort)next == 0)
            next = Interlocked.Increment(ref _sequenceId);
        return (ushort)next;
    }

    /// <summary>
    /// 클라이언트 SequenceId 검증 및 갱신 (Replay Attack 방어)
    /// Actor mailbox 내에서 순차 실행되므로 동시성 안전
    /// </summary>
    public bool ValidateAndUpdateSequenceId(ushort clientSeqId)
    {
        if (clientSeqId == 0)
            return false;

        if (!_seqInitialized)
        {
            _lastClientSequenceId = clientSeqId;
            _seqInitialized = true;
            return true;
        }

        // 양의 반원 방식 wraparound 검증
        int diff = (ushort)(clientSeqId - _lastClientSequenceId);
        if (diff > 0 && diff < 32768)
        {
            _lastClientSequenceId = clientSeqId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 클라이언트 Activity 갱신 (패킷 수신 시 호출)
    /// </summary>
    public void TouchActivity()
    {
        Volatile.Write(ref _lastActivityTicks, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// 새로운 Reconnect Key 발급 (로그인 완료 시 호출)
    /// </summary>
    public Guid RegenerateReconnectKey()
    {
        var newKey = Guid.NewGuid();
        _reconnectKey = newKey;
        _logger.LogDebug("Reconnect key regenerated: ActorId={ActorId}, UserId={UserId}", ActorId, UserId);
        return newKey;
    }

    /// <summary>
    /// Disconnected 상태 진입 시각 기록.
    /// InactivityScanner가 Grace Period 만료를 판단하는 데 사용.
    /// </summary>
    public void MarkDisconnected()
    {
        Volatile.Write(ref _disconnectedTicks, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Disconnected 타임스탬프 초기화 (재접속 성공 시 호출)
    /// </summary>
    public void ClearDisconnected()
    {
        Volatile.Write(ref _disconnectedTicks, 0);
    }

    /// <summary>
    /// Actor mailbox에 비동기 콜백을 push하고 완료를 대기
    /// Scoped DI 컨테이너가 생성되어 콜백에 전달됨
    /// </summary>
    public Task ExecuteAsync(Func<IServiceProvider, Task> action)
    {
        var msg = new CallbackActorMessage(action);
        Push(msg);
        return msg.Completion.Task;
    }

    /// <summary>
    /// 개별 메시지 처리
    /// 미들웨어 파이프라인 실행 → IMessageDispatcher로 비즈니스 로직 위임 → 응답 전송
    /// </summary>
    private async Task ProcessMessageAsync(IActorMessage message)
    {
        TouchActivity();

        // CallbackActorMessage: 외부 스레드에서 Actor 상태에 안전하게 접근하기 위한 콜백 실행
        // Scoped DI 컨테이너를 생성하여 콜백에 전달 (DbContext 등 scoped 서비스 활용 가능)
        if (message is CallbackActorMessage callbackMsg)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await callbackMsg.Callback(scope.ServiceProvider);
                callbackMsg.Completion.TrySetResult();
            }
            catch (Exception ex)
            {
                callbackMsg.Completion.TrySetException(ex);
            }
            return;
        }

        Activity? serverActivity = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            // PacketActorMessage → PacketContext 직접 참조 (변환 없음)
            var context = message is PacketActorMessage pam
                ? pam.Context
                : throw new InvalidOperationException($"Unexpected message type: {message.GetType().Name}");

            // Actor 참조를 PacketContext에 설정 (SendNtf 등에서 사용)
            context.Actor = this;

            // PacketContext를 Scoped DI에 등록 (Controller에서 직접 주입 가능)
            var holder = scope.ServiceProvider.GetService<PacketContextHolder>();
            if (holder != null)
                holder.Context = context;

            // OpenTelemetry: GameServer.Process Activity (샘플링 기반)
            // HasListeners == false이면 sampler가 drop → Activity 생성/string alloc 스킵
            if (TelemetryHelper.Source.HasListeners())
            {
                var msgId = ExtractMsgId(message.Payload);
                var msgName = AutoGeneratedParsers.GetNameById(msgId);
                var parentContext = context.ParentActivityContext;
                serverActivity = parentContext.HasValue
                    ? TelemetryHelper.Source.StartActivity(msgName, ActivityKind.Consumer, parentContext.Value)
                    : TelemetryHelper.Source.StartActivity(msgName, ActivityKind.Consumer);

                if (serverActivity != null)
                {
                    serverActivity.SetTag("rpc.system", "tcp");
                    serverActivity.SetTag("rpc.method", msgName);
                    serverActivity.SetTag("rpc.message.id", msgId);
                    serverActivity.SetTag("session.id", context.SessionId);
                    serverActivity.SetTag("user.id", UserId);
                }
            }

            // 미들웨어 파이프라인 실행 (로깅, 성능 측정, 예외 처리 등)
            await _pipeline.ExecuteAsync(scope.ServiceProvider, context);

            // 미들웨어가 응답을 전송하지 않았으면 디스패처로 비즈니스 로직 실행
            if (!context.IsCompleted)
            {
                var response = await _dispatcher.DispatchAsync(scope.ServiceProvider, this, message);

                if (response != null && context.SendResponse != null)
                {
                    context.SendResponse(
                        context.GatewayNodeId,
                        context.SessionId,
                        response,
                        context.RequestId,
                        NextSequenceId());
                }
            }

            serverActivity?.SetTag("messaging.result", context.IsCompleted ? "middleware" : "dispatched");
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogWarning(ex,
                "Scope factory disposed during message processing (shutting down): ActorId={ActorId}",
                ActorId);
        }
        finally
        {
            serverActivity?.Dispose();

            // Msg 버퍼 반환 (Zero-Copy: Protobuf 역직렬화 완료 후 해제)
            if (message is PacketActorMessage pam2)
                pam2.Context.Dispose();
        }
    }

    /// <summary>
    /// Payload에서 MsgId 추출
    /// Wire format: [EndPointHeader(8)][GameHeader(MsgId(4)+SequenceId(2)+RequestId(2))][Proto]
    /// </summary>
    private static int ExtractMsgId(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < EndPointHeader.SizeOf + sizeof(int))
            return -1;

        return BinaryPrimitives.ReadInt32LittleEndian(payload.Span[EndPointHeader.SizeOf..]);
    }

    public void Dispose()
    {
        _mailbox.Dispose();
    }
}
