using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
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
/// Gateway ↔ GameServer GameSessionChannel 버스
/// RouterSocket으로 모든 GameServer와 양방향 통신 (Data Plane Only)
/// 서버간 제어(Control) 패킷은 Node Service Mesh (Control Plane)에서 처리됨
/// Service Discovery를 통한 동적 연결 지원
/// </summary>
public class GamePacketRouter : IDisposable
{
    private readonly ILogger<GamePacketRouter> _logger;
    private readonly GatewayOptions _options;
    private readonly SessionMapper _sessionMapper;
    private readonly GatewaySessionRegistry _sessionRegistry;

    private RouterSocket? _router;
    private NetMQPoller? _poller;
    private NetMQQueue<GSCMessageEnvelope>? _sendQueue;
    private readonly CancellationTokenSource _cts = new();

    // 라운드 로빈 인덱스 (미고정 세션용)
    private int _roundRobinIndex;
    private readonly ConcurrentDictionary<long, string> _connectedGameServers = new();
    private readonly ConcurrentDictionary<long, byte[]> _gameServerIdentities = new();

    // 라운드로빈용 캐시 배열 (변경 시에만 재생성, 읽기 시 O(1))
    private volatile long[] _gameServerNodeIdCache = [];
    private readonly Lock _cacheLock = new();

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
        _logger.LogInformation("Starting GameSessionChannel Router...");

        _router = new RouterSocket();
        _router.Options.RouterMandatory = false; // 죽은 노드로 전송 시 무시

        // Identity 설정 (Gateway-{NodeId})
        var identity = $"Gateway-{_options.GatewayNodeId}";
        _router.Options.Identity = Encoding.UTF8.GetBytes(identity);

        _sendQueue = new NetMQQueue<GSCMessageEnvelope>();
        _sendQueue.ReceiveReady += OnSendReady;

        // 수신 처리
        _router.ReceiveReady += OnReceiveReady;

        _poller = new NetMQPoller {_router, _sendQueue};
        _poller.RunAsync();

        _logger.LogInformation("GameSessionChannel Router started with identity: {Identity}. Waiting for discovery via Node Mesh Events.", identity);
    }

    public void ConnectToGameServer(long nodeId, string gameServerEndpoint)
    {
        if (_router == null)
            return;

        // 이미 연결된 경우 스킵
        if (_connectedGameServers.ContainsKey(nodeId))
        {
            _logger.LogDebug("GameServer-{NodeId} already connected, skipping", nodeId);
            return;
        }

        var identity = $"GameServer-{nodeId}";

        try
        {
            _router.Connect(gameServerEndpoint);
            _connectedGameServers[nodeId] = identity;
            _gameServerIdentities[nodeId] = Encoding.UTF8.GetBytes(identity);

            UpdateNodeIdCache();

            _logger.LogInformation("Connected to {Identity} DataPlane Mesh ({Endpoint})",
                identity, gameServerEndpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to DataPlane Mesh {Identity} ({Endpoint})",
                identity, gameServerEndpoint);
        }
    }

    public void DisconnectFromGameServer(long nodeId)
    {
        if (_router == null)
            return;

        // 해당 GameServer에 pin된 모든 세션에 에러 알림
        foreach (var session in _sessionRegistry.GetAll())
        {
            if (session.PinnedGameServerNodeId == nodeId)
            {
                session.SendError((short)ErrorCode.GatewayGameServerShutdown);
                session.Unpin();

                _logger.LogInformation(
                    "GameServer-{NodeId} shutdown: notified SocketId={SocketId}",
                    nodeId, session.SocketId);
            }
        }

        if (_connectedGameServers.TryRemove(nodeId, out _))
        {
            _gameServerIdentities.TryRemove(nodeId, out _);
            UpdateNodeIdCache();

            // NetMQ RouterSocket은 명시적 Disconnect 메서드가 없음
            // 연결 목록에서만 제거 (RouterMandatory=false로 전송 실패 시 무시)
            _logger.LogInformation("Disconnected from GameServer-{NodeId}", nodeId);
        }
    }

    /// <summary>
    /// 라운드로빈 캐시 배열 갱신 (GameServer 연결/해제 시에만 호출)
    /// </summary>
    private void UpdateNodeIdCache()
    {
        lock (_cacheLock)
        {
            _gameServerNodeIdCache = _connectedGameServers.Keys.ToArray();
        }
    }

    private void OnReceiveReady(object? sender, NetMQSocketEventArgs e)
    {
        // Router-to-Router: [SourceIdentity][Payload]
        // Payload = [GSCHeader][EndPointHeader][responseData]
        var identityMsg = new Msg();
        var payloadMsg = new Msg();

        identityMsg.InitEmpty();
        payloadMsg.InitEmpty();

        try
        {
            e.Socket.Receive(ref identityMsg);
            if (!identityMsg.HasMore) return;

            e.Socket.Receive(ref payloadMsg);

            var payloadSpan = payloadMsg.Slice();
            if (!NetHeaderHelper.TryRead<GSCHeader>(payloadSpan, out var header))
            {
                _logger.LogWarning("Invalid GameSessionChannel packet (payload too small)");
                return;
            }

            var data = NetHeaderHelper.GetPayload<GSCHeader>(payloadSpan);

            switch (header.Type)
            {
                case GscMessageType.ServerPacket:
                    HandleServerPacket(header, data);
                    break;

                default:
                    _logger.LogWarning("Unexpected message type from GameServer: {Type}", header.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing GameSessionChannel message");
        }
        finally
        {
            identityMsg.Close();
            payloadMsg.Close();
        }
    }

    private void HandleServerPacket(GSCHeader header, ReadOnlySpan<byte> payload)
    {
        _logger.LogDebug("HandleServerPacket: SessionId={SessionId}, PayloadSize={Size}",
            header.SessionId, payload.Length);

        // GameServer → Gateway: SessionId로 클라이언트 세션 조회
        if (!_sessionRegistry.TryGetBySessionId(header.SessionId, out var session))
        {
            _logger.LogWarning("Client session not found: SessionId={SessionId}", header.SessionId);
            return;
        }

        // Outbound 파이프라인: 압축 → 암호화 (순서 중요: compress-then-encrypt)
        var currentPayload = payload;
        byte[]? compressedBuffer = null;

        try
        {
            // Step 1: 선택적 압축 (FlagCompressed 힌트)
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
                        // 압축 스킵 시 FlagCompressed 힌트 제거 (클라이언트가 압축 패킷으로 오인하지 않도록)
                        ClearCompressedFlag(currentPayload);
                    }
                }
            }

            // Step 2: 선택적 암호화 (FlagEncrypted 힌트 또는 세션 암호화 활성)
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

    /// <summary>
    /// EndPointHeader에서 FlagCompressed를 제거 (in-place)
    /// 압축 힌트가 있었지만 실제 압축이 스킵된 경우 사용
    /// 기반 버퍼(NetMQ Msg)가 mutable이므로 Unsafe.AsRef 사용 안전
    /// </summary>
    private static void ClearCompressedFlag(ReadOnlySpan<byte> payload)
    {
        // Flags 필드 오프셋: TotalLength(4) + ErrorCode(2) = 6
        const int flagsOffset = sizeof(int) + sizeof(short);
        ref byte flags = ref Unsafe.AsRef(in payload[flagsOffset]);
        flags = (byte)(flags & ~EndPointHeader.FlagCompressed);
    }

    /// <summary>
    /// 클라이언트 패킷을 GameServer로 포워딩 (Session-Based Routing)
    /// </summary>
    public void ForwardToGameServer(ReadOnlySpan<byte> userPacket, long targetNodeId, long sessionId)
    {
        if (_router == null || _poller == null || _sendQueue == null)
        {
            _logger.LogWarning("P2P bus not started");
            return;
        }

        // NetMQ 풀에서 Unmanaged 메모리 블록 할당 (GC 0)
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

            // OpenTelemetry 컨텍스트 주입 (TraceId 전파)
            TelemetryHelper.InjectContext(ref header);

            // Mesh Header 직렬화 및 클라이언트 데이터 복사를 동기적으로 수행 (버퍼 재사용 문제 회피)
            MemoryMarshal.Write(span, in header);
            userPacket.CopyTo(span.Slice(GSCHeader.SizeOf));

            // 워커 쓰레드(GatewaySession.OnReceived)에서는 큐에 삽입만 수행
            var envelope = new GSCMessageEnvelope(targetNodeId, ref msg);
            _sendQueue.Enqueue(envelope);
        }
        catch (TerminatingException ex)
        {
            _logger.LogError(ex, "Failed to enqueue packet to GameServer-{NodeId}", targetNodeId);
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

    private void OnSendReady(object? sender, NetMQQueueEventArgs<GSCMessageEnvelope> e)
    {
        Debug.Assert(_router != null);

        // Poller 쓰레드 루프 안에서 비동기로 실행됨
        while (e.Queue.TryDequeue(out var envelope, TimeSpan.Zero))
        {
            if (envelope == null) continue;

            using (envelope)
            {
                if (!_gameServerIdentities.TryGetValue(envelope.TargetNodeId, out var identityBytes))
                {
                    _logger.LogWarning("Identity not found for GameServer-{NodeId} on send", envelope.TargetNodeId);
                    continue;
                }

                // MsgDisposable로 자동 리소스 관리
                using var identityMsg = new MsgDisposable(identityBytes);

                try
                {
                    _router.Send(ref identityMsg.GetRef(), true);
                    _router.Send(ref envelope.Payload, false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to route packet to GameServer-{NodeId}", envelope.TargetNodeId);
                }
            }
        }
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

        _poller?.Dispose();
        _sendQueue?.Dispose();
        _router?.Dispose();
        _logger.LogInformation("GameSessionChannel Router stopped");
    }
}