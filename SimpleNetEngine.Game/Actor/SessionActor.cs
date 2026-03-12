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
    private long _sequenceId;
    private long _lastActivityTicks;
    private long _disconnectedTicks;
    private Guid _reconnectKey;

    public long ActorId { get; set; }
    public long UserId { get; set; }
    public long GatewayNodeId { get; private set; }
    public ActorState Status { get; set; }
    public Dictionary<string, object> State { get; } = [];
    public long SequenceId => _sequenceId;
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

        _sequenceId = 0;
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
    /// 재접속 시 라우팅 정보 갱신
    /// </summary>
    public void UpdateRouting(long gatewayNodeId)
    {
        GatewayNodeId = gatewayNodeId;

        _logger.LogDebug(
            "Actor routing updated: ActorId={ActorId}, Gateway={GatewayNodeId}",
            ActorId, gatewayNodeId);
    }

    /// <summary>
    /// 다음 시퀀스 번호 발급 (원자적 증가)
    /// </summary>
    public long NextSequenceId()
    {
        return Interlocked.Increment(ref _sequenceId);
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

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            // PacketActorMessage → PacketContext 직접 참조 (변환 없음)
            var context = message is PacketActorMessage pam
                ? pam.Context
                : throw new InvalidOperationException($"Unexpected message type: {message.GetType().Name}");

            // OpenTelemetry: MsgId 기반 메시지별 처리 시간 측정
            // ParentActivityContext로 Gateway → GameServer.Process → Actor.Process 트레이스 체인 유지
            var msgId = ExtractMsgId(message.Payload);
            var msgName = AutoGeneratedParsers.GetNameById(msgId);
            var parentContext = context.ParentActivityContext;
            using var activity = parentContext.HasValue
                ? TelemetryHelper.Source.StartActivity($"Actor.Process {msgName}", ActivityKind.Internal, parentContext.Value)
                : TelemetryHelper.Source.StartActivity($"Actor.Process {msgName}", ActivityKind.Internal);
            if (activity != null)
            {
                activity.SetTag("actor.id", ActorId);
                activity.SetTag("actor.user.id", UserId);
                activity.SetTag("messaging.message.id", msgId);
                activity.SetTag("messaging.message.name", msgName);
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
                        context.RequestId);
                }
            }

            activity?.SetTag("messaging.result", context.IsCompleted ? "middleware" : "dispatched");
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogWarning(ex,
                "Scope factory disposed during message processing (shutting down): ActorId={ActorId}",
                ActorId);
        }
        finally
        {
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
        if (payload.Length < EndPointHeader.Size + sizeof(int))
            return -1;

        return BinaryPrimitives.ReadInt32LittleEndian(payload.Span[EndPointHeader.Size..]);
    }

    public void Dispose()
    {
        _mailbox.Dispose();
    }
}
