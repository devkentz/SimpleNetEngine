using System.Buffers;
using System.Buffers.Binary;
using NetCoreServer;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Protocol.Memory;
using SimpleNetEngine.Protocol.Packets;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Google.Protobuf;
using Internal.Protocol;
using SimpleNetEngine.Node.Network;
using SimpleNetEngine.Infrastructure.Telemetry;
using SimpleNetEngine.Protocol.Utils;
using SimpleNetEngine.ProtoGenerator;

// ReSharper disable All

namespace SimpleNetEngine.Gateway.Network;

/// <summary>
/// 클라이언트 TCP 세션
/// Dumb Proxy 원칙: 패킷 내용을 분석하지 않고 투명하게 포워딩만 함
/// Session-Based Routing: 각 세션 객체가 자신의 라우팅 정보를 메모리에 저장
///
/// SessionId는 Gateway가 OnConnected 시점에 즉시 발급하여 모든 패킷에 항상 포함됨.
/// sessionId == 0인 패킷은 존재하지 않음 (설계 원칙).
///
/// 암호화 책임은 SessionCrypto에 위임 (has-a).
/// _crypto == null이면 암호화 비활성 (ECDH 키 생성 자체 생략).
/// </summary>
public class GatewaySession : TcpSession
{
    private readonly ILogger _logger;
    private readonly GamePacketRouter _packetRouter;
    private readonly INodeSender _nodeSender;
    private readonly UniqueIdGenerator _idGenerator;
    private readonly long _gatewayNodeId;

    // --- TCP 수신 버퍼 (패킷 분할/병합 처리) ---
    private readonly ArrayPoolBufferWriter _receiveBuffer = new();

    // --- 암호화 (has-a, nullable) ---
    private readonly SessionCrypto? _crypto;

    // --- Rate Limit (초당 패킷 수 제한) ---
    private readonly int _maxPacketsPerSecond;
    private long _rateLimitWindowTicks;
    private int _rateLimitCount;

    public Guid SocketId => this.Id;

    /// <summary>
    /// 고정된 GameServer NodeId (0 = 미고정)
    /// </summary>
    private long _pinnedGameServerNodeId;
    public long PinnedGameServerNodeId => Volatile.Read(ref _pinnedGameServerNodeId);

    /// <summary>
    /// 게임 세션 ID (OnConnected 시점에 즉시 발급, 항상 > 0)
    /// </summary>
    private long _gameSessionId;
    public long GameSessionId => Volatile.Read(ref _gameSessionId);

    public GatewaySession(
        TcpServer server,
        ILogger logger,
        GamePacketRouter packetRouter,
        INodeSender nodeSender,
        long gatewayNodeId,
        UniqueIdGenerator idGenerator,
        int maxPacketsPerSecond,
        SessionCrypto? crypto = null) : base(server)
    {
        _logger = logger;
        _packetRouter = packetRouter;
        _nodeSender = nodeSender;
        _idGenerator = idGenerator;
        _gatewayNodeId = gatewayNodeId;
        _maxPacketsPerSecond = maxPacketsPerSecond;
        _crypto = crypto;
    }

    protected override void OnConnected()
    {
        _logger.LogDebug("[Session:{SocketId}] Connected from {RemoteEndPoint}", SocketId, AnonymizeIp(Socket.RemoteEndPoint));

        _packetRouter.RegisterClientSession(SocketId, this);

        var targetNodeId = _packetRouter.GetNextGameServerNodeId();

        if (targetNodeId != 0)
        {
            // SessionId를 Gateway에서 즉시 발급하여 NodeId와 함께 고정
            var sessionId = GenerateSessionId();
            Volatile.Write(ref _pinnedGameServerNodeId, targetNodeId);
            Volatile.Write(ref _gameSessionId, sessionId);

            _logger.LogDebug("[Session:{SocketId}] Pinned: NodeId={NodeId}, SessionId={SessionId}",
                SocketId, targetNodeId, sessionId);

            // SessionId → Session 매핑 등록 (GameServer가 SocketId 없이 SessionId로 응답 라우팅)
            _packetRouter.RegisterSessionId(sessionId, this);

            // GameServer에 SessionId 전달하여 Actor 생성 요청 (fire-and-forget)
            // GameServer가 Actor 생성 후 ReadyToHandshakeNtf를 GSC 경유로 클라이언트에 전송
            _ = NotifyGameServerNewUserAsync(targetNodeId, sessionId);

        }
        else
        {
            Kick(ErrorCode.GatewayGameServerUnreachable);
        }
    }

    private async Task NotifyGameServerNewUserAsync(long targetNodeId, long sessionId)
    {
        try
        {
            var pubKey = _crypto?.GetEphemeralPublicKey() ?? [];
            var signature = _crypto?.GetEphemeralSignature();

            var req = new ServiceMeshNewUserNtfReq
            {
                GatewayNodeId = _gatewayNodeId,
                SessionId = sessionId,
                GatewayEphemeralPublicKey = ByteString.CopyFrom(pubKey),
                GatewayEphemeralSignature = signature != null
                    ? ByteString.CopyFrom(signature)
                    : ByteString.Empty
            };

            var res = await _nodeSender.RequestAsync<ServiceMeshNewUserNtfReq, ServiceMeshNewUserNtfRes>(
                NodePacket.ServerActorId,
                targetNodeId,
                req);

            if (res.Success)
            {
                _logger.LogDebug(
                    "NtfNewUser success: SocketId={SocketId}, SessionId={SessionId}, GameServer={NodeId}",
                    SocketId, sessionId, targetNodeId);
            }
            else
            {
                _logger.LogError(
                    "NtfNewUser failed: SocketId={SocketId}, SessionId={SessionId}, GameServer={NodeId}",
                    SocketId, sessionId, targetNodeId);
                Kick(ErrorCode.GatewayGameServerUnreachable);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("NtfNewUser cancelled: SocketId={SocketId}, GameServer={NodeId}", SocketId, targetNodeId);
        }
        catch (TimeoutException ex)
        {
            _logger.LogError(ex, "NtfNewUser timed out: SocketId={SocketId}, GameServer={NodeId}", SocketId, targetNodeId);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "NtfNewUser failed (node unavailable): SocketId={SocketId}, GameServer={NodeId}", SocketId, targetNodeId);
        }
    }

    private long GenerateSessionId()
    {
        return _idGenerator.NextId();
    }

    private string AnonymizeIp(System.Net.EndPoint? endpoint)
    {
        if (endpoint is System.Net.IPEndPoint ipEndpoint)
        {
            var ip = ipEndpoint.Address;
            var bytes = ip.GetAddressBytes();

            if (bytes.Length == 4)  // IPv4
                return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.xxx";
            else  // IPv6
                return $"{ip:X}::/64";  // Mask last 64 bits
        }
        return "unknown";
    }


    protected override void OnDisconnected()
    {
        var sessionId = Volatile.Read(ref _gameSessionId);
        var pinnedNodeId = Volatile.Read(ref _pinnedGameServerNodeId);
        _logger.LogDebug("[Session:{SocketId}] Disconnected (NodeId={NodeId}, SessionId={SessionId})",
            SocketId, pinnedNodeId, sessionId);

        // 리소스 정리
        _receiveBuffer.Dispose();
        _crypto?.Dispose();

        Unpin();
        _packetRouter.OnClientDisconnected(SocketId, sessionId);

        // GameServer에 disconnect 알림 (fire-and-forget)
        if (pinnedNodeId != 0 && sessionId != 0)
            _ = NotifyGameServerClientDisconnectedAsync(pinnedNodeId, sessionId);
    }

    private async Task NotifyGameServerClientDisconnectedAsync(long targetNodeId, long sessionId)
    {
        try
        {
            var req = new ServiceMeshClientDisconnectedNtfReq
            {
                SessionId = sessionId,
                GatewayNodeId = _gatewayNodeId
            };

            await _nodeSender.RequestAsync<ServiceMeshClientDisconnectedNtfReq, ServiceMeshClientDisconnectedNtfRes>(
                NodePacket.ServerActorId,
                targetNodeId,
                req);

            _logger.LogDebug(
                "ClientDisconnected sent: SessionId={SessionId}, GameServer={NodeId}",
                sessionId, targetNodeId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ClientDisconnected cancelled: SessionId={SessionId}, GameServer={NodeId}", sessionId, targetNodeId);
        }
        catch (TimeoutException ex)
        {
            _logger.LogError(ex, "ClientDisconnected timed out: SessionId={SessionId}, GameServer={NodeId}", sessionId, targetNodeId);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "ClientDisconnected failed (node unavailable): SessionId={SessionId}, GameServer={NodeId}", sessionId, targetNodeId);
        }
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
        // 1. 입력 검증
        if (size <= 0 || size > PacketDefine.MaxPacketSize ||
            offset < 0 || offset > int.MaxValue || size > int.MaxValue)
        {
            Log.InvalidPacket(_logger, offset, size, SocketId);
            Disconnect();
            return;
        }

        var intOffset = (int)offset;
        var intSize = (int)size;

        if (intOffset + intSize > buffer.Length)
        {
            Log.PacketBoundaryViolation(_logger, offset, size, buffer.Length, SocketId);
            Disconnect();
            return;
        }

        // 2. 수신 버퍼에 누적 (TCP 스트림 → 패킷 프레이밍)
        _receiveBuffer.Write(buffer.AsSpan(intOffset, intSize));

        // 3. 완전한 패킷이 모일 때까지 반복 처리
        while (true)
        {
            var bufferedSpan = _receiveBuffer.WrittenSpan;

            // TotalLength를 읽으려면 최소 4바이트 필요
            if (bufferedSpan.Length < sizeof(int))
                break;

            int totalSize = BinaryPrimitives.ReadInt32LittleEndian(bufferedSpan);
            if (totalSize <= 0 || totalSize > PacketDefine.MaxPacketSize)
            {
                Log.InvalidTotalLength(_logger, totalSize, SocketId);
                Disconnect();
                return;
            }

            // 완전한 패킷이 아직 도착하지 않음
            if (bufferedSpan.Length < totalSize)
                break;

            // Rate Limit 체크 (zero-allocation)
            if (_maxPacketsPerSecond > 0)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(_rateLimitWindowTicks, now);
                if (elapsed.TotalSeconds >= 1.0)
                {
                    _rateLimitWindowTicks = now;
                    _rateLimitCount = 0;
                }

                if (++_rateLimitCount > _maxPacketsPerSecond)
                {
                    Log.RateLimitExceeded(_logger, SocketId, _rateLimitCount);
                    Kick(ErrorCode.GatewayRateLimitExceeded);
                    return;
                }
            }

            // 완전한 패킷 하나 추출
            var packetSpan = bufferedSpan[..totalSize];
            ProcessSinglePacket(packetSpan);
            _receiveBuffer.ReadAdvance(totalSize);
        }
    }

    /// <summary>
    /// 완전한 단일 패킷 처리: 복호화 → 압축 해제 → GameServer 포워딩
    /// </summary>
    private void ProcessSinglePacket(ReadOnlySpan<byte> packetSpan)
    {
        // Atomic snapshot: Race Condition 방지 (TOCTOU 해결)
        long targetNodeId = Volatile.Read(ref _pinnedGameServerNodeId);
        long sessionId = Volatile.Read(ref _gameSessionId);

        if (targetNodeId == 0 || sessionId == 0)
        {
            var hasEpHeader = NetHeaderHelper.HasHeader<EndPointHeader>(packetSpan);
            ref readonly var epHeader = ref (hasEpHeader
                ? ref NetHeaderHelper.Peek<EndPointHeader>(packetSpan)
                : ref Unsafe.NullRef<EndPointHeader>());

            Log.PacketDroppedNotReady(_logger, SocketId, targetNodeId, sessionId, packetSpan.Length);
            return;
        }

        using var activity = TelemetryHelper.Source.HasListeners()
            ? TelemetryHelper.Source.StartActivity("GSC.Recv", ActivityKind.Server)
            : null;
        if (activity != null)
        {
            activity.SetTag("rpc.system", "tcp");
            activity.SetTag("session.id", sessionId);
            activity.SetTag("message.size", packetSpan.Length);
        }

        try
        {
            var currentSpan = packetSpan;
            byte[]? decryptedBuffer = null;
            byte[]? decompressedBuffer = null;

            try
            {
                if (NetHeaderHelper.HasHeader<EndPointHeader>(currentSpan))
                {
                    ref readonly var header = ref NetHeaderHelper.Peek<EndPointHeader>(currentSpan);

                    // Step 1: 복호화 (암호화가 마지막에 적용되었으므로 먼저 해제)
                    if (header.IsEncrypted)
                    {
                        if (_crypto == null || !_crypto.IsActive)
                        {
                            Log.EncryptedNoKey(_logger, SocketId);
                            Disconnect();
                            return;
                        }

                        if (!_crypto.TryDecrypt(currentSpan, out decryptedBuffer, out var decryptedLength))
                        {
                            Log.DecryptionFailed(_logger, SocketId, sessionId);
                            Disconnect();
                            return;
                        }

                        currentSpan = decryptedBuffer.AsSpan(0, decryptedLength);
                    }

                    // Step 2: 압축 해제 (원본 헤더의 플래그로 판단 — 암호화/복호화는 Compressed 플래그에 영향 없음)
                    if (header.IsCompressed)
                    {
                        if (!PacketCompressor.TryDecompress(currentSpan,
                                out decompressedBuffer, out var decompressedLength))
                        {
                            Log.DecompressionFailed(_logger, SocketId, sessionId);
                            Disconnect();
                            return;
                        }

                        currentSpan = decompressedBuffer.AsSpan(0, decompressedLength);
                    }
                }

                // Observability: 복호화/압축 해제 후 MsgId로 메시지 이름 태깅
                if (activity != null
                    && NetHeaderHelper.HasHeader<EndPointHeader>(currentSpan)
                    && NetHeaderHelper.TryRead<GameHeader>(
                        NetHeaderHelper.GetPayload<EndPointHeader>(currentSpan), out var gameHeader))
                {
                    var msgName = AutoGeneratedParsers.GetNameById(gameHeader.MsgId);
                    activity.DisplayName = $"GSC.Recv {msgName}";
                    activity.SetTag("rpc.method", msgName);
                    activity.SetTag("rpc.message.id", gameHeader.MsgId);
                }

                _packetRouter.ForwardToGameServer(currentSpan, targetNodeId, sessionId);
            }
            finally
            {
                PacketEncryptor.ReturnBuffer(decryptedBuffer);
                PacketCompressor.ReturnBuffer(decompressedBuffer);
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex,
                "Failed to forward packet (routing error): SocketId={SocketId}, NodeId={NodeId}, SessionId={SessionId}",
                SocketId, targetNodeId, sessionId);
            Kick(ErrorCode.GatewayGameServerUnreachable);
        }
    }

    protected override void OnError(System.Net.Sockets.SocketError error)
    {
        _logger.LogError("Session error: SocketId={SocketId}, Error={Error}", Id, error);
    }

    /// <summary>
    /// 재라우팅 (중복 로그인이나 재접속 시 다른 GameServer로 변경)
    /// </summary>
    public void Reroute(long newTargetNodeId)
    {
        var oldNodeId = Volatile.Read(ref _pinnedGameServerNodeId);
        Volatile.Write(ref _pinnedGameServerNodeId, newTargetNodeId);

        _logger.LogDebug(
            "Session rerouted: SocketId={SocketId}, OldNodeId={OldNodeId}, NewNodeId={NewNodeId}, GameSessionId={SessionId}",
            SocketId, oldNodeId, newTargetNodeId, Volatile.Read(ref _gameSessionId));
    }

    /// <summary>
    /// 세션 고정 해제 (연결 종료 시)
    /// </summary>
    public void Unpin()
    {
        Volatile.Write(ref _pinnedGameServerNodeId, 0);
        Volatile.Write(ref _gameSessionId, 0);

        _logger.LogDebug("Session unpinned: SocketId={SocketId}", SocketId);
    }

    /// <summary>
    /// GameServer로부터 받은 응답을 클라이언트로 전송
    /// </summary>
    public void SendFromGameServer(ReadOnlySpan<byte> data)
    {
        Log.SendFromGameServer(_logger, SocketId, data.Length, IsConnected);

        if (!SendAsync(data))
        {
            Log.SendToClientFailed(_logger, SocketId, GameSessionId, data.Length);
            Disconnect();
        }
    }

    /// <summary>
    /// Outbound 암호화 후 클라이언트로 전송
    /// </summary>
    public void SendEncrypted(ReadOnlySpan<byte> data)
    {
        if (_crypto == null || !_crypto.IsActive)
        {
            SendFromGameServer(data);
            return;
        }

        if (_crypto.TryEncrypt(data, out var encryptedBuffer, out var encryptedLength))
        {
            try
            {
                SendFromGameServer(encryptedBuffer.AsSpan(0, encryptedLength));
            }
            finally
            {
                PacketEncryptor.ReturnBuffer(encryptedBuffer);
            }
        }
        else
        {
            SendFromGameServer(data);
        }
    }

    /// <summary>
    /// 암호화 활성 여부
    /// </summary>
    public bool IsEncryptionActive => _crypto?.IsActive ?? false;

    /// <summary>
    /// 클라이언트 ECDH 공개키로 SharedSecret 도출 → 암호화 활성화.
    /// SessionCrypto에 위임.
    /// </summary>
    public void DeriveAndActivateEncryption(byte[] clientEphemeralPublicKeyDer)
    {
        if (_crypto == null)
        {
            _logger.LogWarning("DeriveAndActivateEncryption called but encryption is disabled: SocketId={SocketId}", SocketId);
            return;
        }

        _crypto.DeriveAndActivateEncryption(clientEphemeralPublicKeyDer);
        _logger.LogDebug("Encryption activated: SocketId={SocketId}", SocketId);
    }


    /// <summary>
    /// 세션 강제 종료: 에러코드 전송 → 연결 해제
    /// Gateway I/O 계층 메커니즘 (비즈니스 판단은 호출자 책임)
    /// </summary>
    public void Kick(ErrorCode reason)
    {
        Log.Kick(_logger, SocketId, reason, GameSessionId);

        if (reason != ErrorCode.None)
            SendError((short)reason);

        Disconnect();
    }

    /// <summary>
    /// 에러 전용 패킷을 클라이언트로 직접 전송 (GameServer 경유 없음)
    /// Wire format: [EndPointHeader(ErrorCode)][GameHeader(zeros)]
    /// </summary>
    public void SendError(short errorCode)
    {
        Span<byte> buffer = stackalloc byte[EndPointHeader.SizeOf + GameHeader.SizeOf];

        var endPointHeader = new EndPointHeader
        {
            TotalLength = EndPointHeader.SizeOf + GameHeader.SizeOf,
            ErrorCode = errorCode
        };
        MemoryMarshal.Write(buffer, in endPointHeader);
        // GameHeader 영역은 stackalloc으로 이미 0 초기화됨

        if (!SendAsync(buffer))
        {
            Log.SendErrorFailed(_logger, SocketId, errorCode);
        }
    }

}
