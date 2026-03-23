using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using SimpleNetEngine.Game.Options;
using Microsoft.Extensions.Logging;
using NetMQ;
using NetMQ.Sockets;
using SimpleNetEngine.Infrastructure.NetMQ;
using Microsoft.Extensions.Options;
using Google.Protobuf;
using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Middleware;
using System.Runtime.InteropServices;
using SimpleNetEngine.Infrastructure.Telemetry;
using SimpleNetEngine.Protocol.Packets;
using SimpleNetEngine.ProtoGenerator;

namespace SimpleNetEngine.Game.Network;


/// <summary>
/// GameServer의 GameSessionChannel Listener (Dual-Socket 패턴)
///
/// Recv Socket (Port N): Gateway → GameServer 방향 (클라이언트 패킷 수신 전용)
/// Send Socket (Port N+1): GameServer → Gateway 방향 (서버 응답 전송 전용)
///
/// 각 소켓은 전용 IO 쓰레드에서 동작하여 recv/send 간 경합을 완전히 제거합니다.
/// Channel&lt;T&gt;로 Actor 쓰레드 → Send 쓰레드 간 안전한 큐잉을 수행합니다.
/// </summary>
public class GameSessionChannelListener : IDisposable
{
    private readonly ILogger<GameSessionChannelListener> _logger;
    private readonly GameOptions _options;
    private readonly IClientPacketHandler _packetHandler;

    // Dual RouterSocket
    private RouterSocket? _recvRouter;
    private RouterSocket? _sendRouter;

    // Recv: NetMQPoller 이벤트 기반 (자체 쓰레드), Send: 전용 Thread
    private NetMQPoller? _recvPoller;
    private Thread? _sendThread;
    private readonly CancellationTokenSource _cts = new();

    // Channel<T> 기반 송신 큐 (멀티스레드 안전, Actor 쓰레드에서 write)
    private readonly Channel<MeshMessageEnvelope> _sendChannel =
        Channel.CreateUnbounded<MeshMessageEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,              // Send IO 쓰레드만 읽기
            SingleWriter = false,             // 여러 Actor 쓰레드에서 쓰기
            AllowSynchronousContinuations = false // IOCP 쓰레드에서 RouterSocket 접근 방지
        });

    // Zero-allocation 전송을 위한 Identity Bytes 캐싱
    private readonly ConcurrentDictionary<long, byte[]> _gatewayIdentities = new();

    /// <summary>
    /// 수신 소켓 바인딩 포트 (Gateway → GameServer)
    /// </summary>
    public int BoundRecvPort { get; private set; }

    /// <summary>
    /// 송신 소켓 바인딩 포트 (GameServer → Gateway)
    /// </summary>
    public int BoundSendPort { get; private set; }

    /// <summary>
    /// 하위 호환: 수신 포트 반환
    /// </summary>
    public int BoundPort => BoundRecvPort;

    private class MeshMessageEnvelope : IDisposable
    {
        public long TargetGatewayId { get; }
        public Msg Payload;

        public MeshMessageEnvelope(long targetGatewayId, ref Msg payload)
        {
            TargetGatewayId = targetGatewayId;
            Payload = new Msg();
            Payload.Move(ref payload);
        }

        public void Dispose()
        {
            if (Payload.IsInitialised)
                Payload.Close();
        }
    }

    public GameSessionChannelListener(
        ILogger<GameSessionChannelListener> logger,
        IOptions<GameOptions> options,
        IClientPacketHandler packetHandler)
    {
        _logger = logger;
        _options = options.Value;
        _packetHandler = packetHandler;
    }

    public Task StartAsync()
    {
        var identity = $"GameServer-{_options.GameNodeId}";
        var identityBytes = Encoding.UTF8.GetBytes(identity);

        // --- Recv Router (Port N) ---
        _recvRouter = new RouterSocket();
        _recvRouter.Options.RouterMandatory = false;
        _recvRouter.Options.ReceiveHighWatermark = 50000;
        _recvRouter.Options.Identity = identityBytes;

        BoundRecvPort = TryBindToPort(_recvRouter, _options.GameSessionChannelPort, "Recv");

        // --- Send Router (Port N+1) ---
        _sendRouter = new RouterSocket();
        _sendRouter.Options.RouterMandatory = false;
        _sendRouter.Options.SendHighWatermark = 50000;
        _sendRouter.Options.Identity = identityBytes;

        var preferredSendPort = _options.GameSessionChannelSendPort > 0
            ? _options.GameSessionChannelSendPort
            : BoundRecvPort + 1;
        BoundSendPort = TryBindToPort(_sendRouter, preferredSendPort, "Send");

        // --- Recv: NetMQPoller (자체 쓰레드에서 이벤트 기반 수신) ---
        _recvRouter.ReceiveReady += (_, _) =>
        {
            while (TryReceivePacket()) { }
        };
        _recvPoller = new NetMQPoller { _recvRouter };
        _recvPoller.RunAsync();

        // --- Send Thread (전용 쓰레드에서 Channel 소비) ---
        _sendThread = new Thread(SendLoop) { Name = "GSC-Send", IsBackground = true };
        _sendThread.Start();

        _logger.LogInformation(
            "GameSessionChannel started: identity={Identity}, recv={RecvPort}, send={SendPort}",
            identity, BoundRecvPort, BoundSendPort);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Send Thread: 전용 쓰레드에서 Channel 데이터 즉시 전송
    /// Channel SingleReader=true → 동시 접근 쓰레드 항상 1개
    /// </summary>
    private void SendLoop()
    {
        _logger.LogDebug("Send thread started: {ThreadId}", Environment.CurrentManagedThreadId);

        try
        {
            var reader = _sendChannel.Reader;
            while (WaitToRead(reader))
            {
                while (reader.TryRead(out var envelope))
                {
                    using (envelope)
                        SendEnvelopeToRouter(envelope);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (TerminatingException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Send thread");
        }

        _logger.LogDebug("Send thread stopped");

        // Channel.Reader.WaitToReadAsync → 동기 블로킹 대기
        // 데이터가 이미 있으면 ValueTask가 동기 완료되어 할당 없음
        bool WaitToRead(ChannelReader<MeshMessageEnvelope> r)
        {
            var vt = r.WaitToReadAsync(_cts.Token);
            return vt.IsCompletedSuccessfully ? vt.Result : vt.AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Non-blocking 패킷 수신 (zero-allocation Msg API 사용)
    /// </summary>
    private bool TryReceivePacket()
    {
        var identityMsg = new Msg();
        var payloadMsg = new Msg();
        identityMsg.InitEmpty();
        payloadMsg.InitEmpty();

        try
        {
            if (!_recvRouter!.TryReceive(ref identityMsg, TimeSpan.Zero))
                return false;

            if (!identityMsg.HasMore)
                return true; // consumed but invalid (no payload frame)

            _recvRouter.TryReceive(ref payloadMsg, TimeSpan.Zero);
            ProcessReceivedPacket(ref payloadMsg);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving GameSessionChannel packet");
            return false;
        }
        finally
        {
            identityMsg.Close();
            payloadMsg.Close();
        }
    }

    /// <summary>
    /// 수신된 패킷 처리 (최소한의 파싱 후 Actor로 전달)
    /// </summary>
    private void ProcessReceivedPacket(ref Msg payloadMsg)
    {
        var fullPayload = payloadMsg.Slice();
        if (!NetHeaderHelper.HasHeader<GSCHeader>(fullPayload))
        {
            _logger.LogWarning("[GameServer] Invalid GSC packet (payload too small: {Size} bytes, need {Need}, hex={Hex})",
                fullPayload.Length, GSCHeader.SizeOf, Convert.ToHexString(fullPayload[..Math.Min(fullPayload.Length, 32)]));
            return;
        }

        ref readonly var header = ref NetHeaderHelper.Peek<GSCHeader>(fullPayload);

        if (header.Type != GscMessageType.ClientPacket)
        {
            _logger.LogWarning("Unexpected message type: {Type}", header.Type);
            return;
        }

        var clientData = NetHeaderHelper.GetPayload<GSCHeader>(fullPayload);

        if (!NetHeaderHelper.HasHeader<EndPointHeader>(clientData))
        {
            _logger.LogWarning("Invalid payload size from Gateway");
            return;
        }

        var afterEp = NetHeaderHelper.GetPayload<EndPointHeader>(clientData);
        if (!NetHeaderHelper.HasHeader<GameHeader>(afterEp))
        {
            _logger.LogWarning("Invalid payload size from Gateway");
            return;
        }

        ref readonly var gameHeader = ref NetHeaderHelper.Peek<GameHeader>(afterEp);

        // TraceId/SpanId만 추출 (Activity 생성은 Actor 쓰레드에서 수행)
        var traceContext = TelemetryHelper.ExtractTraceContext(header);

        // Zero-Copy: Msg 소유권을 PacketContext로 이전
        var context = new PacketContext
        {
            GatewayNodeId = header.GatewayNodeId,
            SessionId = header.SessionId,
            SequenceId = gameHeader.SequenceId,
            RequestId = gameHeader.RequestId,
            SendResponse = SendResponse,
            StartTime = DateTimeOffset.UtcNow,
            ParentActivityContext = traceContext
        };
        context.TransferPayload(ref payloadMsg, GSCHeader.SizeOf);

        _packetHandler.HandlePacket(context);
    }

    /// <summary>
    /// 송신 envelope을 Send RouterSocket으로 전송 (Send IO 쓰레드에서만 호출)
    /// </summary>
    private void SendEnvelopeToRouter(MeshMessageEnvelope envelope)
    {
        var identityBytes = GetGatewayIdentityBytes(envelope.TargetGatewayId);
        using var identityMsg = new MsgDisposable(identityBytes);

        try
        {
            _sendRouter!.Send(ref identityMsg.GetRef(), true);
            _sendRouter!.Send(ref envelope.Payload, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to route packet to Gateway-{NodeId}", envelope.TargetGatewayId);
        }
    }

    /// <summary>
    /// 지정된 포트부터 시작하여 사용 가능한 포트에 바인딩합니다.
    /// </summary>
    private int TryBindToPort(RouterSocket router, int preferredPort, string socketName)
    {
        if (!_options.AllowDynamicPort)
        {
            router.Bind($"tcp://*:{preferredPort}");
            return preferredPort;
        }

        const int maxRetries = 100;
        var currentPort = preferredPort;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                router.Bind($"tcp://*:{currentPort}");
                if (currentPort != preferredPort)
                    _logger.LogInformation("GSC {Socket} bound to dynamic port: {Port} (preferred: {Preferred})",
                        socketName, currentPort, preferredPort);
                return currentPort;
            }
            catch (AddressAlreadyInUseException)
            {
                _logger.LogDebug("GSC {Socket} port {Port} is in use, trying next port", socketName, currentPort);
                currentPort++;
            }
        }

        throw new InvalidOperationException(
            $"Could not find an available port for GSC {socketName} after {maxRetries} attempts starting from {preferredPort}");
    }

    private byte[] GetGatewayIdentityBytes(long gatewayNodeId)
    {
        if (_gatewayIdentities.TryGetValue(gatewayNodeId, out var bytes))
        {
            return bytes;
        }

        var newBytes = Encoding.UTF8.GetBytes($"Gateway-{gatewayNodeId}");
        _gatewayIdentities[gatewayNodeId] = newBytes;
        return newBytes;
    }

    /// <summary>
    /// 송신 큐에 패킷 추가 (모든 쓰레드에서 호출 가능 — Channel은 thread-safe)
    /// </summary>
    private void EnqueuePacket(long gatewayNodeId, ref Msg msg)
    {
        var envelope = new MeshMessageEnvelope(gatewayNodeId, ref msg);
        if (!_sendChannel.Writer.TryWrite(envelope))
        {
            envelope.Dispose();
            _logger.LogError("Failed to enqueue packet to Gateway-{NodeId} (channel full)", gatewayNodeId);
        }
    }

    /// <summary>
    /// Zero-Copy 응답 전송: Response(IMessage)를 Msg에 직접 직렬화
    /// Wire format: [GSCHeader][EndPointHeader][GameHeader][Protobuf Payload]
    /// 에러 응답 (response.IsError): [GSCHeader][EndPointHeader(ErrorCode)][GameHeader] — Payload 없음
    /// </summary>
    public virtual void SendResponse(long gatewayNodeId, long sessionId, Response response, ushort requestId, ushort sequenceId)
    {
        try
        {
            if (response.IsError)
            {
                SendErrorResponse(gatewayNodeId, sessionId, (short)response.ErrorCode, requestId, sequenceId);
                return;
            }

            var protoMessage = response.Message!;
            var msgId = AutoGeneratedParsers.GetIdByInstance(protoMessage);
            var payloadSize = protoMessage.CalculateSize();

            var totalSize = GSCHeader.SizeOf + EndPointHeader.SizeOf + GameHeader.SizeOf + payloadSize;

            var msg = new Msg();
            msg.InitPool(totalSize);

            var span = msg.Slice();
            int offset = 0;

            var gscHeader = new GSCHeader
            {
                Type = GscMessageType.ServerPacket,
                GatewayNodeId = gatewayNodeId,
                SourceNodeId = _options.GameNodeId,
                SessionId = sessionId
            };
            MemoryMarshal.Write(span[offset..], in gscHeader);
            offset += GSCHeader.SizeOf;

            byte flags = 0;
            if (response.IsCompress) flags |= EndPointHeader.FlagCompressed;
            if (response.IsEncryption) flags |= EndPointHeader.FlagEncrypted;
            var endPointHeader = new EndPointHeader
            {
                TotalLength = EndPointHeader.SizeOf + GameHeader.SizeOf + payloadSize,
                Flags = flags
            };

            MemoryMarshal.Write(span[offset..], in endPointHeader);
            offset += EndPointHeader.SizeOf;

            var gameHeader = new GameHeader
            {
                MsgId = msgId,
                RequestId = requestId,
                SequenceId = sequenceId
            };
            gameHeader.Write(span[offset..]);
            offset += GameHeader.SizeOf;

            protoMessage.WriteTo(span.Slice(offset, payloadSize));

            EnqueuePacket(gatewayNodeId, ref msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send response to Gateway-{NodeId}", gatewayNodeId);
        }
    }

    /// <summary>
    /// 에러 전용 응답 전송 (Payload 없음)
    /// </summary>
    private void SendErrorResponse(long gatewayNodeId, long sessionId, short errorCode, ushort requestId, ushort sequenceId = 0)
    {
        var totalSize = GSCHeader.SizeOf + EndPointHeader.SizeOf + GameHeader.SizeOf;
        var msg = new Msg();
        msg.InitPool(totalSize);

        var span = msg.Slice();
        int offset = 0;

        var gscHeader = new GSCHeader
        {
            Type = GscMessageType.ServerPacket,
            GatewayNodeId = gatewayNodeId,
            SourceNodeId = _options.GameNodeId,
            SessionId = sessionId
        };
        MemoryMarshal.Write(span[offset..], in gscHeader);
        offset += GSCHeader.SizeOf;

        var endPointHeader = new EndPointHeader
        {
            TotalLength = EndPointHeader.SizeOf + GameHeader.SizeOf,
            ErrorCode = errorCode
        };
        MemoryMarshal.Write(span[offset..], in endPointHeader);
        offset += EndPointHeader.SizeOf;

        var gameHeader = new GameHeader { RequestId = requestId, SequenceId = sequenceId };
        gameHeader.Write(span[offset..]);

        EnqueuePacket(gatewayNodeId, ref msg);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _recvPoller?.Stop();
        _sendChannel.Writer.Complete();
        _sendThread?.Join(TimeSpan.FromSeconds(3));
        _recvPoller?.Dispose();
        _recvRouter?.Dispose();
        _sendRouter?.Dispose();
        _logger.LogInformation("GameSessionChannel Listener stopped (dual socket)");
    }
}
