using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
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
/// GameServer의 GameSessionChannel Listener
/// RouterSocket으로 모든 Gateway와 양방향 통신
/// Gateway가 Node Mesh Event를 통해 이 노드의 GameSessionChannelPort로 Connect를 수행함
/// </summary>
public class GameSessionChannelListener : IDisposable
{
    private readonly ILogger<GameSessionChannelListener> _logger;
    private readonly GameOptions _options;
    private readonly IClientPacketHandler _packetHandler;

    private RouterSocket? _router;
    private NetMQPoller? _poller;
    private NetMQQueue<MeshMessageEnvelope>? _sendQueue;
    private readonly CancellationTokenSource _cts = new();

    // Zero-allocation 전송을 위한 Identity Bytes 캐싱
    private readonly ConcurrentDictionary<long, byte[]> _gatewayIdentities = new();

    /// <summary>
    /// 실제 바인딩된 포트 (동적 할당 시 원래 포트와 다를 수 있음)
    /// </summary>
    public int BoundPort { get; private set; }

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
        _logger.LogInformation("Starting GameSessionChannel Listener on port {Port}...", _options.GameSessionChannelPort);

        _router = new RouterSocket();
        _router.Options.RouterMandatory = false; // 죽은 큐(연결 끊김)로 전송 시 이면 무시

        // Identity 설정 (GameServer-{NodeId})
        var identity = $"GameServer-{_options.GameNodeId}";
        _router.Options.Identity = Encoding.UTF8.GetBytes(identity);

        // 바인딩 (Gateway들이 이 포트로 연결함 - Control Plane에서 포트를 받아 Connect수행)
        BoundPort = TryBindToPort(_options.GameSessionChannelPort);

        // 비동기 송신 큐 초기화 (Thread 안전)
        _sendQueue = new NetMQQueue<MeshMessageEnvelope>();
        _sendQueue.ReceiveReady += OnSendReady;

        // 수신 처리
        _router.ReceiveReady += OnReceiveReady;

        _poller = new NetMQPoller {_router, _sendQueue};
        _poller.RunAsync();

        _logger.LogInformation("GameSessionChannel Listener started with identity: {Identity}. Waiting for Gateway connections.", identity);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 지정된 포트부터 시작하여 사용 가능한 포트에 바인딩합니다.
    /// </summary>
    private int TryBindToPort(int preferredPort)
    {
        if (!_options.AllowDynamicPort)
        {
            _router!.Bind($"tcp://*:{preferredPort}");
            return preferredPort;
        }

        const int maxRetries = 100;
        var currentPort = preferredPort;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                _router!.Bind($"tcp://*:{currentPort}");
                if (currentPort != preferredPort)
                    _logger.LogInformation("GameSessionChannel bound to dynamic port: {Port} (preferred: {Preferred})", currentPort, preferredPort);
                return currentPort;
            }
            catch (AddressAlreadyInUseException)
            {
                _logger.LogDebug("GameSessionChannel port {Port} is in use, trying next port", currentPort);
                currentPort++;
            }
        }

        throw new InvalidOperationException(
            $"Could not find an available port after {maxRetries} attempts starting from {preferredPort}");
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

    private void OnReceiveReady(object? sender, NetMQSocketEventArgs e)
    {
        // Router-to-Router: [SourceIdentity][Payload]
        // Payload = [GSCHeader][ClientData]
        var identityMsg = new Msg();
        var payloadMsg = new Msg();

        identityMsg.InitEmpty();
        payloadMsg.InitEmpty();

        try
        {
            e.Socket.Receive(ref identityMsg);
            if (!identityMsg.HasMore) return;

            e.Socket.Receive(ref payloadMsg);

            var fullPayload = payloadMsg.Slice();
            if (!NetHeaderHelper.HasHeader<GSCHeader>(fullPayload))
            {
                _logger.LogWarning("Invalid GameSessionChannel packet (payload too small)");
                return;
            }

            ref readonly var header = ref NetHeaderHelper.Peek<GSCHeader>(fullPayload);

            if (header.Type != GscMessageType.ClientPacket)
            {
                _logger.LogWarning("Unexpected message type: {Type}", header.Type);
                return;
            }

            var clientData = NetHeaderHelper.GetPayload<GSCHeader>(fullPayload);

            // Client 패킷은 [EndPointHeader][GameHeader][Payload] 구조이므로 EndPointHeader를 건너뜀
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

            // OpenTelemetry: MsgId 기반 메시지 이름으로 Activity 시작
            var msgName = AutoGeneratedParsers.GetNameById(gameHeader.MsgId);
            using var activity = TelemetryHelper.ExtractAndStartActivity(
                header, $"GameServer.Process {msgName}", ActivityKind.Consumer);
            if (activity != null)
            {
                activity.SetTag("messaging.operation", "process");
                activity.SetTag("messaging.message.id", gameHeader.MsgId);
                activity.SetTag("messaging.message.name", msgName);
                activity.SetTag("gateway.node.id", header.GatewayNodeId);
                activity.SetTag("session.id", header.SessionId);
                activity.SetTag("packet.size", fullPayload.Length - GSCHeader.SizeOf);
            }

            // Zero-Copy: Msg 소유권을 PacketContext로 이전 (ToArray() 제거)
            // PacketContext.Dispose()에서 Msg.Close()로 버퍼 반환
            var context = new PacketContext
            {
                GatewayNodeId = header.GatewayNodeId,
                SessionId = header.SessionId,
                SequenceId = gameHeader.SequenceId,
                RequestId = gameHeader.RequestId,
                SendResponse = SendResponse,
                StartTime = DateTimeOffset.UtcNow,
                ParentActivityContext = activity?.Context
            };
            context.TransferPayload(ref payloadMsg, GSCHeader.SizeOf);

            _packetHandler.HandlePacket(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing GameSessionChannel packet");
        }
        finally
        {
            identityMsg.Close();
            payloadMsg.Close();
        }
    }

    private void EnqueuePacket(long gatewayNodeId, ref Msg msg)
    {
        if (_sendQueue == null)
        {
            msg.Close();
            return;
        }

        try
        {
            var envelope = new MeshMessageEnvelope(gatewayNodeId, ref msg);
            _sendQueue.Enqueue(envelope);
        }
        catch (TerminatingException ex)
        {
            _logger.LogError(ex, "Failed to enqueue packet to Gateway-{NodeId}", gatewayNodeId);
            if (msg.IsInitialised)
                msg.Close();
        }
    }

    private void OnSendReady(object? sender, NetMQQueueEventArgs<MeshMessageEnvelope> e)
    {
        Debug.Assert(_router != null);

        // NetMQPoller 쓰레드 루프 단일 진입점 (Thread-Safe)
        while (e.Queue.TryDequeue(out var envelope, TimeSpan.Zero))
        {
            if (envelope == null) continue;

            using (envelope)
            {
                var identityBytes = GetGatewayIdentityBytes(envelope.TargetGatewayId);

                // MsgDisposable로 자동 리소스 관리
                using var identityMsg = new MsgDisposable(identityBytes);

                try
                {
                    _router.Send(ref identityMsg.GetRef(), true);
                    _router.Send(ref envelope.Payload, false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to route packet to Gateway-{NodeId}", envelope.TargetGatewayId);
                }
            }
        }
    }


    /// <summary>
    /// Zero-Copy 응답 전송: Response(IMessage)를 Msg에 직접 직렬화
    /// Wire format: [GSCHeader][EndPointHeader][GameHeader][Protobuf Payload]
    /// 에러 응답 (response.IsError): [GSCHeader][EndPointHeader(ErrorCode)][GameHeader] — Payload 없음
    /// </summary>
    public void SendResponse(long gatewayNodeId, long sessionId, Response response, ushort requestId, ushort sequenceId)
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

            // 단일 Msg에 모든 헤더 + Protobuf payload 직접 기록 (Zero-Copy)
            var totalSize = GSCHeader.SizeOf + EndPointHeader.SizeOf + GameHeader.SizeOf + payloadSize;

            var msg = new Msg();
            msg.InitPool(totalSize);

            var span = msg.Slice();
            int offset = 0;

            // 1. GSCHeader (내부 라우팅용)
            var gscHeader = new GSCHeader
            {
                Type = GscMessageType.ServerPacket,
                GatewayNodeId = gatewayNodeId,
                SourceNodeId = _options.GameNodeId,
                SessionId = sessionId
            };
            MemoryMarshal.Write(span[offset..], in gscHeader);
            offset += GSCHeader.SizeOf;

            // 2. EndPointHeader (Gateway → Client TCP 프레이밍)
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

            // 3. GameHeader (MsgId, RequestId, SequenceId)
            var gameHeader = new GameHeader
            {
                MsgId = msgId,
                RequestId = requestId,
                SequenceId = sequenceId
            };
            gameHeader.Write(span[offset..]);
            offset += GameHeader.SizeOf;

            // 4. Protobuf payload 직접 기록 (Zero-Copy: IMessage → Span)
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
    /// Wire format: [GSCHeader][EndPointHeader(ErrorCode)][GameHeader(RequestId)]
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
        _poller?.Dispose();
        _router?.Dispose();
        _sendQueue?.Dispose();
        _logger.LogInformation("GameSessionChannel Listener stopped");
    }
}