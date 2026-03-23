using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using SimpleNetEngine.Gateway.Options;
using SimpleNetEngine.Gateway.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetMQ;
using NetMQ.Sockets;
using SimpleNetEngine.Infrastructure.NetMQ;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SimpleNetEngine.Infrastructure.Telemetry;
using SimpleNetEngine.Protocol.Packets;
using ErrorCode = SimpleNetEngine.Protocol.Packets.ErrorCode;

namespace SimpleNetEngine.Gateway.Network;

/// <summary>
/// Gateway ↔ GameServer GameSessionChannel 버스 (Dual-Socket 패턴)
///
/// Send RouterSocket: Gateway → GameServer 수신 포트 (Port N) — identity 기반 특정 GameServer 라우팅
/// Recv RouterSocket: GameServer 송신 포트 (Port N+1) → Gateway
///
/// 단일 IO 쓰레드에서 양쪽 소켓을 배치 처리 (소켓이 분리되어 경합 없음)
/// Service Discovery를 통한 동적 연결 지원
/// </summary>
public class GamePacketRouter : IDisposable
{
    private readonly ILogger<GamePacketRouter> _logger;
    private readonly GatewayOptions _options;
    private readonly SessionMapper _sessionMapper;
    private readonly GatewaySessionRegistry _sessionRegistry;

    // Dual RouterSocket (identity 기반 라우팅 유지 — DealerSocket은 round-robin이라 특정 GameServer 지정 불가)
    private RouterSocket? _sendRouter;   // Gateway → GameServer recv port (send only)
    private RouterSocket? _recvRouter;   // GameServer send port → Gateway (recv only)

    // Recv: NetMQPoller 이벤트 기반 (자체 쓰레드), Send: 전용 Thread
    private NetMQPoller? _recvPoller;
    private Thread? _sendThread;
    private readonly CancellationTokenSource _cts = new();

    // Channel<T> 기반 송신 큐 (멀티스레드 안전, GatewaySession IOCP 쓰레드에서 write)
    private readonly Channel<GSCMessageEnvelope> _sendChannel =
        Channel.CreateUnbounded<GSCMessageEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,              // Send IO 쓰레드만 읽기
            SingleWriter = false,             // 여러 IOCP 쓰레드에서 쓰기
            AllowSynchronousContinuations = false
        });

    // 라운드 로빈 인덱스 (미고정 세션용)
    private int _roundRobinIndex;
    private readonly ConcurrentDictionary<long, GameServerConnection> _connectedGameServers = new();
    private readonly ConcurrentDictionary<long, byte[]> _gameServerIdentities = new();

    // 라운드로빈용 캐시 배열 (변경 시에만 재생성, 읽기 시 O(1))
    private volatile long[] _gameServerNodeIdCache = [];
    private readonly Lock _cacheLock = new();

    /// <summary>
    /// GameServer 연결 정보 (send/recv 엔드포인트)
    /// </summary>
    private class GameServerConnection
    {
        public required string SendEndpoint { get; init; }
        public required string RecvEndpoint { get; init; }
    }

    private class GSCMessageEnvelope : IDisposable
    {
        public long TargetNodeId { get; }
        public Msg Payload;

        public GSCMessageEnvelope(long targetNodeId, ref Msg payload)
        {
            TargetNodeId = targetNodeId;
            Payload = new Msg();
            Payload.Move(ref payload);
        }

        public void Dispose()
        {
            if (Payload.IsInitialised)
                Payload.Close();
        }
    }

    public GamePacketRouter(
        ILogger<GamePacketRouter> logger,
        IOptions<GatewayOptions> options,
        SessionMapper sessionMapper,
        GatewaySessionRegistry sessionRegistry)
    {
        _logger = logger;
        _options = options.Value;
        _sessionMapper = sessionMapper;
        _sessionRegistry = sessionRegistry;
    }

    public void StartAsync()
    {
        _logger.LogInformation("Starting GameSessionChannel Router (dual-socket)...");

        var identity = Encoding.UTF8.GetBytes($"Gateway-{_options.GatewayNodeId}");

        // --- Send RouterSocket (Gateway → GameServer recv port) ---
        _sendRouter = new RouterSocket();
        _sendRouter.Options.RouterMandatory = true;
        _sendRouter.Options.Identity = identity;
        _sendRouter.Options.SendHighWatermark = 50000;

        // --- Recv RouterSocket (GameServer send port → Gateway) ---
        _recvRouter = new RouterSocket();
        _recvRouter.Options.RouterMandatory = false;
        _recvRouter.Options.Identity = identity;
        _recvRouter.Options.ReceiveHighWatermark = 50000;

        // --- Recv: NetMQPoller (자체 쓰레드에서 이벤트 기반 수신) ---
        _recvRouter.ReceiveReady += (_, _) =>
        {
            while (TryReceivePacket()) { }
        };
        _recvPoller = new NetMQPoller { _recvRouter };
        _recvPoller.RunAsync();

        // --- Send Thread (전용 쓰레드에서 Channel 소비) ---
        _sendThread = new Thread(SendLoop) { Name = "GSC-GW-Send", IsBackground = true };
        _sendThread.Start();

        _logger.LogInformation("GameSessionChannel Router started (dual-socket)");
    }

    /// <summary>
    /// Send Thread: 전용 쓰레드에서 Channel 데이터 즉시 전송
    /// Channel SingleReader=true → 동시 접근 쓰레드 항상 1개
    /// </summary>
    private void SendLoop()
    {
        _logger.LogDebug("Gateway Send thread started: {ThreadId}", Environment.CurrentManagedThreadId);

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
            _logger.LogError(ex, "Error in Gateway Send thread");
        }

        _logger.LogDebug("Gateway Send thread stopped");

        bool WaitToRead(ChannelReader<GSCMessageEnvelope> r)
        {
            var vt = r.WaitToReadAsync(_cts.Token);
            return vt.IsCompletedSuccessfully ? vt.Result : vt.AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Non-blocking 패킷 수신 (RouterSocket — identity + payload 2-frame)
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
                return true;

            _recvRouter.TryReceive(ref payloadMsg, TimeSpan.Zero);
            ProcessReceivedPacket(ref payloadMsg);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving GameSessionChannel message");
            return false;
        }
        finally
        {
            identityMsg.Close();
            payloadMsg.Close();
        }
    }

    /// <summary>
    /// 수신된 패킷 처리
    /// </summary>
    private void ProcessReceivedPacket(ref Msg payloadMsg)
    {
        var payloadSpan = payloadMsg.Slice();
        if (!NetHeaderHelper.TryRead<GSCHeader>(payloadSpan, out var header))
        {
            _logger.LogWarning("[Gateway] Invalid GSC packet (payload too small: {Size} bytes, need {Need}, hex={Hex})",
                payloadSpan.Length, GSCHeader.SizeOf, Convert.ToHexString(payloadSpan[..Math.Min(payloadSpan.Length, 32)]));
            return;
        }

        var data = NetHeaderHelper.GetPayload<GSCHeader>(payloadSpan);
        HandleServerPacket(in header, data);
    }

    /// <summary>
    /// 송신 envelope을 Send RouterSocket으로 전송 (IO 쓰레드에서만 호출)
    /// Identity frame으로 특정 GameServer에 라우팅
    /// </summary>
    private void SendEnvelopeToRouter(GSCMessageEnvelope envelope)
    {
        if (!_gameServerIdentities.TryGetValue(envelope.TargetNodeId, out var identityBytes))
        {
            _logger.LogWarning("Identity not found for GameServer-{NodeId} on send", envelope.TargetNodeId);
            NotifySessionError(envelope, ErrorCode.GatewayGameServerUnreachable);
            return;
        }

        using var identityMsg = new MsgDisposable(identityBytes);

        try
        {
            _sendRouter!.Send(ref identityMsg.GetRef(), true);
            _sendRouter!.Send(ref envelope.Payload, false);
        }
        catch (HostUnreachableException)
        {
            _logger.LogWarning("GameServer-{NodeId} unreachable (RouterMandatory)", envelope.TargetNodeId);
            NotifySessionError(envelope, ErrorCode.GatewayGameServerUnreachable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send packet to GameServer-{NodeId}", envelope.TargetNodeId);
        }
    }

    /// <summary>
    /// GSCHeader에서 SessionId를 추출하여 클라이언트에 에러 패킷 전송
    /// </summary>
    private void NotifySessionError(GSCMessageEnvelope envelope, ErrorCode errorCode)
    {
        var payloadSpan = envelope.Payload.Slice();
        if (NetHeaderHelper.TryRead<GSCHeader>(payloadSpan, out var header)
            && _sessionRegistry.TryGetBySessionId(header.SessionId, out var session))
        {
            session.Kick(errorCode);
        }
    }

    /// <summary>
    /// GameServer에 dual-socket 연결
    /// </summary>
    /// <param name="nodeId">GameServer NodeId</param>
    /// <param name="recvEndpoint">GameServer 수신 포트 (Port N) — Gateway가 send로 연결</param>
    /// <param name="sendEndpoint">GameServer 송신 포트 (Port N+1) — Gateway가 recv로 연결</param>
    public void ConnectToGameServer(long nodeId, string recvEndpoint, string sendEndpoint)
    {
        if (_sendRouter == null || _recvRouter == null)
            return;

        if (_connectedGameServers.ContainsKey(nodeId))
        {
            _logger.LogDebug("GameServer-{NodeId} already connected, skipping", nodeId);
            return;
        }

        var identity = $"GameServer-{nodeId}";

        try
        {
            // Send first (Gateway → GameServer recv port)
            _sendRouter.Connect(recvEndpoint);
            // Then recv (GameServer send port → Gateway)
            _recvRouter.Connect(sendEndpoint);

            _connectedGameServers[nodeId] = new GameServerConnection
            {
                SendEndpoint = recvEndpoint,
                RecvEndpoint = sendEndpoint
            };
            _gameServerIdentities[nodeId] = Encoding.UTF8.GetBytes(identity);

            UpdateNodeIdCache();

            _logger.LogInformation(
                "Connected to GameServer-{NodeId} DataPlane: send→{SendEp}, recv←{RecvEp}",
                nodeId, recvEndpoint, sendEndpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to GameServer-{NodeId} DataPlane", nodeId);
        }
    }

    public void DisconnectFromGameServer(long nodeId)
    {
        if (_sendRouter == null || _recvRouter == null)
            return;

        foreach (var session in _sessionRegistry.GetAll())
        {
            if (session.PinnedGameServerNodeId == nodeId)
            {
                session.Kick(ErrorCode.GatewayGameServerShutdown);
            }
        }

        if (_connectedGameServers.TryRemove(nodeId, out var conn))
        {
            _gameServerIdentities.TryRemove(nodeId, out _);

            try
            {
                // Disconnect recv first, then send
                _recvRouter.Disconnect(conn.RecvEndpoint);
                _sendRouter.Disconnect(conn.SendEndpoint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disconnecting from GameServer-{NodeId}", nodeId);
            }

            UpdateNodeIdCache();
            _logger.LogInformation("Disconnected from GameServer-{NodeId}", nodeId);
        }
    }

    private void UpdateNodeIdCache()
    {
        lock (_cacheLock)
        {
            _gameServerNodeIdCache = _connectedGameServers.Keys.ToArray();
        }
    }

    private void HandleServerPacket(in GSCHeader header, ReadOnlySpan<byte> payload)
    {
        Log.HandleServerPacket(_logger, header.SessionId, payload.Length);

        if (!_sessionRegistry.TryGetBySessionId(header.SessionId, out var session))
        {
            Log.SessionNotFound(_logger, header.SessionId);
            return;
        }

        var currentPayload = payload;
        byte[]? compressedBuffer = null;

        try
        {
            if (_options.EnableCompression && NetHeaderHelper.HasHeader<EndPointHeader>(currentPayload))
            {
                ref readonly var epHeader = ref NetHeaderHelper.Peek<EndPointHeader>(currentPayload);
                if (epHeader.IsCompressed)
                {
                    if (PacketCompressor.TryCompress(currentPayload, _options.CompressionThreshold,
                            out compressedBuffer, out var compressedLength))
                    {
                        currentPayload = compressedBuffer.AsSpan(0, compressedLength);
                    }
                    else
                    {
                        ClearCompressedFlag(currentPayload);
                    }
                }
            }

            if (NetHeaderHelper.HasHeader<EndPointHeader>(currentPayload))
            {
                ref readonly var epHeader = ref NetHeaderHelper.Peek<EndPointHeader>(currentPayload);
                if (epHeader.IsEncrypted && session.IsEncryptionActive)
                {
                    session.SendEncrypted(currentPayload);
                    return;
                }
            }

            session.SendFromGameServer(currentPayload);
        }
        finally
        {
            PacketCompressor.ReturnBuffer(compressedBuffer);
        }
    }

    private static void ClearCompressedFlag(ReadOnlySpan<byte> payload)
    {
        const int flagsOffset = sizeof(int) + sizeof(short);
        ref byte flags = ref Unsafe.AsRef(in payload[flagsOffset]);
        flags = (byte)(flags & ~EndPointHeader.FlagCompressed);
    }

    /// <summary>
    /// 클라이언트 패킷을 GameServer로 포워딩 (Session-Based Routing)
    /// GatewaySession의 IOCP 쓰레드에서 호출됨 — Channel.Writer.TryWrite는 thread-safe
    /// </summary>
    public void ForwardToGameServer(ReadOnlySpan<byte> userPacket, long targetNodeId, long sessionId)
    {
        if (_sendRouter == null)
        {
            _logger.LogWarning("P2P bus not started");
            return;
        }

        var msg = new Msg();
        msg.InitPool(GSCHeader.SizeOf + userPacket.Length);

        try
        {
            var span = msg.Slice();

            var header = new GSCHeader
            {
                Type = GscMessageType.ClientPacket,
                GatewayNodeId = _options.GatewayNodeId,
                SourceNodeId = _options.GatewayNodeId,
                SessionId = sessionId
            };

            TelemetryHelper.InjectContext(ref header);

            MemoryMarshal.Write(span, in header);
            userPacket.CopyTo(span.Slice(GSCHeader.SizeOf));

            var envelope = new GSCMessageEnvelope(targetNodeId, ref msg);
            if (!_sendChannel.Writer.TryWrite(envelope))
            {
                envelope.Dispose();
                _logger.LogError("Failed to enqueue packet to GameServer-{NodeId}", targetNodeId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forward packet to GameServer-{NodeId}", targetNodeId);
            if (msg.IsInitialised)
                msg.Close();
        }
    }

    public long GetNextGameServerNodeId()
    {
        var cache = _gameServerNodeIdCache;
        if (cache.Length == 0)
            return 0;

        var index = (uint) Interlocked.Increment(ref _roundRobinIndex) % (uint) cache.Length;
        return cache[index];
    }

    public void RegisterClientSession(Guid socketId, GatewaySession session)
    {
        _sessionRegistry.Register(socketId, session);
    }

    public void RegisterSessionId(long sessionId, GatewaySession session)
    {
        _sessionRegistry.RegisterSessionId(sessionId, session);
    }

    public void OnClientDisconnected(Guid socketId, long sessionId)
    {
        _sessionRegistry.TryRemove(socketId);

        if (sessionId != 0)
            _sessionRegistry.RemoveSessionId(sessionId);

        _sessionMapper.RemoveSocket(socketId);
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
        _logger.LogInformation("GameSessionChannel Router stopped (dual-socket)");
    }

}
