using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Game.Protocol;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Protocol.Memory;
using SimpleNetEngine.Client.Config;
using SimpleNetEngine.Client.Network;
using SimpleNetEngine.Protocol.Packets;
using SimpleNetEngine.ProtoGenerator;
using TcpClient = NetCoreServer.TcpClient;

namespace SimpleNetEngine.Client
{
    public enum ClientState
    {
        Disconnected,
        Connected,
    }

    internal class TcpClientImplement(string address, int port) : TcpClient(address, port)
    {
        public event Action? OnConnectedHandler;
        public event Action? OnDisconnectedHandler;
        public event Action<SocketError>? OnErrorHandler;
        public event Action<byte[], long, long>? OnReceivedHandler;

        protected override void OnConnected()
        {
            OnConnectedHandler?.Invoke();
        }

        protected override void OnDisconnected()
        {
            OnDisconnectedHandler?.Invoke();
        }

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            OnReceivedHandler?.Invoke(buffer, offset, size);
        }

        protected override void OnError(SocketError error)
        {
            OnErrorHandler?.Invoke(error);
        }
    }

    /// <summary>
    /// 클라이언트 Outbound 패킷 옵션 (C2S)
    /// 서버 측 Response.UseEncrypt()/UseCompress()의 클라이언트 대칭
    /// </summary>
    public readonly struct SendOptions(bool encrypt = false)
    {
        public static readonly SendOptions Default = new();
        public static readonly SendOptions Encrypted = new(encrypt: true);

        public bool Encrypt { get; init; } = encrypt;
    }

    /// <summary>
    /// Handshake 결과
    /// </summary>
    public record HandshakeResult;

    /// <summary>
    /// 클라이언트 Handshake 처리 인터페이스
    /// ConnectAsync 내부에서 TCP 연결 직후 자동으로 호출됨
    /// </summary>
    public interface IHandshakeHandler
    {
        Task<HandshakeResult> PerformHandshakeAsync(NetClient client, CancellationToken cancellationToken);
    }

    public sealed class NetClient : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ProtoPacketParser _packetParser;
        private readonly RpcRequestManager _rpcManager;
        private readonly NetClientConfig _config;
        private readonly ArrayPoolBufferWriter _receiveBuffer = new();
        private readonly ConcurrentQueue<NetworkPacket> _recvList = [];

        private readonly MessageHandler _handler;
        private readonly IHandshakeHandler _handshakeHandler;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<IMessage>> _messageInterceptors = new();
        private TaskCompletionSource<bool>? _tcpConnectTcs;

        private readonly TcpClientImplement _tcpClientImplement;
        private bool _disposed  = false;
        private ushort _msgSeq;
        private volatile SocketError _lastSocketError = SocketError.Success;

        /// <summary>
        /// 다음 SequenceId 발급 (단조 증가, 0 건너뛰기)
        /// </summary>
        private ushort NextMsgSeq()
        {
            _msgSeq = _msgSeq == ushort.MaxValue ? (ushort)1 : (ushort)(_msgSeq + 1);
            return _msgSeq;
        }

        // --- Idle Ping (Heartbeat) ---
        private long _lastSentTicks;
        private volatile bool _pingEnabled;
        private TimeSpan _pingInterval;

        // --- Time Sync (NTP-style) ---
        private long _lastTimeSyncTicks;
        private volatile bool _timeSyncEnabled;
        private long _serverTimeOffsetMs;
        private long _lastRttMs;

        // --- 암호화 상태 (Outbound C2S만, Inbound S2C는 ProtoPacketParser에서 처리) ---
        private AesGcm? _encryptAesGcm;
        private ulong _encryptCounter;
        private volatile bool _encryptionActive;

        private const int EncTagSize = 16;
        private const int EncNonceSize = 12;
        private const byte DirectionC2S = 0;

        public ClientState State => _tcpClientImplement.IsConnected ? ClientState.Connected : ClientState.Disconnected;

        public event Action? OnConnectedHandler;
        public event Action? OnDisconnectedHandler;
        public event Action<SocketError>? OnErrorHandler;

        /// <summary>
        /// 로그인 성공 후 GameServer가 발급한 ReconnectKey (LoginGameRes에서 수신)
        /// </summary>
        public string? ReconnectKey { get; set; }

        /// <summary>
        /// 현재 클라이언트 SequenceId (재접속 시 ReconnectReq.LastClientSequenceId로 전달)
        /// </summary>
        public ushort CurrentSequenceId => _msgSeq;

        /// <summary>
        /// 재접속 성공 후 서버가 확인한 SequenceId로 동기화
        /// ReconnectRes.ClientNextSequenceId - 1 을 전달하면 다음 NextMsgSeq()가 올바른 값을 반환
        /// </summary>
        public void SyncSequenceId(ushort sequenceId)
        {
            _msgSeq = sequenceId;
        }

        /// <summary>
        /// 암호화 채널 활성화 여부 (ECDH 키 교환 완료 후 true)
        /// 서버가 EnableEncryption=false이면 항상 false.
        /// SendOptions.Encrypted 사용 전에 이 값을 확인할 것.
        /// </summary>
        public bool IsEncryptionActive => _encryptionActive;

        /// <summary>
        /// Idle Ping 활성화 (Handshake 완료 시 서버 옵션으로 자동 호출됨)
        /// intervalMs == 0이면 Ping 비활성화 상태 유지.
        /// </summary>
        public void EnablePing(uint intervalMs = 0)
        {
            if (intervalMs > 0)
                _pingInterval = TimeSpan.FromMilliseconds(intervalMs);

            Volatile.Write(ref _lastSentTicks, Stopwatch.GetTimestamp());
            _pingEnabled = true;
        }

        /// <summary>
        /// TimeSync 활성화 (Handshake 완료 후 호출)
        /// </summary>
        public void EnableTimeSync()
        {
            Volatile.Write(ref _lastTimeSyncTicks, Stopwatch.GetTimestamp());
            _timeSyncEnabled = true;
        }

        /// <summary>
        /// 서버-클라이언트 시간 오프셋 (ms). 서버시간 = 로컬UTC + Offset
        /// </summary>
        public long ServerTimeOffsetMs => Volatile.Read(ref _serverTimeOffsetMs);

        /// <summary>
        /// 마지막 측정 RTT (ms)
        /// </summary>
        public long LastRttMs => Volatile.Read(ref _lastRttMs);

        /// <summary>
        /// 보정된 현재 서버 시간 (UTC, ms)
        /// </summary>
        public long EstimatedServerTimeMs
            => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Volatile.Read(ref _serverTimeOffsetMs);

        private long Sid { get; set; }
        public bool IsDispose { get; set; }

        /// <summary>
        /// NetClient 생성자 (기본 설정 사용)
        /// </summary>
        public NetClient(string address, int port, ILogger logger, MessageHandler handler)
            : this(address, port, logger, handler, null, null)
        {
        }

        /// <summary>
        /// NetClient 생성자 (의존성 주입)
        /// </summary>
        public NetClient(
            string address,
            int port,
            ILogger logger,
            MessageHandler handler,
            IRequestIdGenerator? requestIdGenerator = null,
            NetClientConfig? config = null,
            IHandshakeHandler? handshakeHandler = null)
        {
            _logger = logger;
            _handler = handler;
            _config = config ?? new NetClientConfig();
            _handshakeHandler = handshakeHandler ?? GameHandshakeHandler.CreateFromPemFile(_config.SigningPublicKeyPath);

            _packetParser = new ProtoPacketParser();
            _rpcManager = new RpcRequestManager(requestIdGenerator ?? new SequentialRequestIdGenerator(), _config.RequestTimeout);

            _tcpClientImplement = new TcpClientImplement(address, port)
            {
                OptionNoDelay = _config.NoDelay,
                OptionKeepAlive = _config.KeepAlive
            };

            _tcpClientImplement.OnErrorHandler += OnError;
            _tcpClientImplement.OnConnectedHandler += OnConnected;
            _tcpClientImplement.OnDisconnectedHandler += OnDisconnected;
            _tcpClientImplement.OnReceivedHandler += OnOnReceived;
        }

        /// <summary>
        /// TCP 연결 + Handshake를 수행합니다.
        /// Handshake 완료 후 OnConnectedHandler가 콜백됩니다.
        /// </summary>
        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default, int timeoutMs = 15_000)
        {
            _tcpConnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var timeoutCts = new CancellationTokenSource(timeoutMs);

            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                // 1. Handshake 인터셉터 사전 등록 (TCP 연결 전에 등록해야 race condition 방지)
                //    PerformHandshakeAsync는 내부에서 WaitForMessageAsync로 인터셉터를 등록한 뒤 await로 중단됨
                //    ReadyToHandshakeNtf는 TCP 연결 → Gateway OnConnected → NtfNewUser → GameServer 경유 후 도착하므로
                //    인터셉터 등록이 메시지 도착보다 반드시 먼저 완료됨
                var handshakeTask = _handshakeHandler.PerformHandshakeAsync(this, linkedCts.Token);

                // 2. TCP 연결
                var connectTcs = _tcpConnectTcs;
                _tcpClientImplement.ConnectAsync();
                await connectTcs.Task.WaitAsync(linkedCts.Token);

                // 3. Handshake 완료 대기 (인터셉터가 ReadyToHandshakeNtf 수신 후 HandshakeReq 전송)
                var handshakeResult = await handshakeTask;

                // 4. 암호화는 PerformHandshakeAsync 내부에서 이미 활성화됨
                //    ReconnectKey는 이후 LoginGameRes에서 수신 (앱 레벨에서 설정)
                _logger.LogInformation("Handshake completed{Encrypted}",
                    _encryptionActive ? " (encrypted)" : "");

                // 5. Handshake 완료 후 OnConnected 콜백 (Ping은 EnablePing() 호출 시 활성화)
                OnConnectedHandler?.Invoke();

                return true;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                _logger.LogWarning("Connection timed out after {Timeout}ms", timeoutMs);
                throw new TimeoutException($"Connection timed out after {timeoutMs}ms");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Connection cancelled by user");
                throw;
            }
            catch (SocketException sex)
            {
                _logger.LogError("Connection failed: {Error} ({Code}) [sid:{Sid}]", sex.SocketErrorCode, (int)sex.SocketErrorCode, Sid);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection failed");
                throw;
            }
            finally
            {
                _tcpConnectTcs = null;
            }
        }

        public void Disconnect()
        {
            _tcpClientImplement.Disconnect();
        }

        internal void ActivateEncryption(byte[] aesKey)
        {
            var oldAesGcm = _encryptAesGcm;
            _encryptAesGcm = new AesGcm(aesKey, EncTagSize);
            _encryptCounter = 0;
            _encryptionActive = true;
            oldAesGcm?.Dispose();

            // Parser에도 복호화 키 설정 (S2C 복호화)
            _packetParser.SetDecryptionKey(aesKey);

            CryptographicOperations.ZeroMemory(aesKey);
        }

        public void Send(IMessage packet, SendOptions options = default)
        {
            if (!_tcpClientImplement.IsConnected)
            {
                _logger.LogWarning("Cannot send message - client is disconnected");
                return;
            }

            var msgId = AutoGeneratedParsers.GetIdByInstance(packet);
            if (msgId == -1)
            {
                _logger.LogError("Unknown message type: {MessageType}", packet.GetType().Name);
                return;
            }

            try
            {
                InternalSend(new EndPointHeader(), new GameHeader { MsgId = msgId, SequenceId = NextMsgSeq(), RequestId = 0 }, packet, options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send message - connection may be lost");
            }
        }

        public async Task<TResponse> RequestAsync<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
            where TResponse : IMessage
            where TRequest : IMessage
            => await RequestAsync<TRequest, TResponse>(request, default(SendOptions), cancellationToken);

        public async Task<TResponse> RequestAsync<TRequest, TResponse>(TRequest request, SendOptions options, CancellationToken cancellationToken = default)
            where TResponse : IMessage
            where TRequest : IMessage
        {
            if (!_tcpClientImplement.IsConnected)
            {
                throw new NetClientException(
                    (ushort) InternalErrorCode.Disconnected,
                    "Cannot send request - client is disconnected",
                    request
                );
            }

            var msgId = AutoGeneratedParsers.GetIdByInstance(request);
            if (msgId == -1)
            {
                _logger.LogError("Unknown message type: {MessageType}", request.GetType().Name);
                throw new NetClientException(
                    (ushort) InternalErrorCode.InvalidMessage,
                    $"Unknown message type: {request.GetType().Name}",
                    request
                );
            }

            try
            {
                return (TResponse) await _rpcManager.SendRequestAsync(
                    requestId => InternalSend(new EndPointHeader(), new GameHeader { MsgId = msgId, SequenceId = NextMsgSeq(), RequestId = requestId }, request, options),
                    cancellationToken
                );
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(
                    "Request timed out - MsgId: {MsgId}, Type: {MessageType}",
                    msgId,
                    request.GetType().Name
                );
                throw new NetClientException(
                    (ushort) InternalErrorCode.RequestTimeout,
                    $"Request timed out after {_config.RequestTimeout.TotalMilliseconds}ms - MsgId: {msgId}",
                    ex,
                    request
                );
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("RequestId collision"))
            {
                _logger.LogError(ex, "RequestId collision occurred");
                throw;
            }
        }

        /// <summary>
        /// 특정 MsgId의 서버 푸시 메시지를 1회 대기합니다.
        /// Handshake 중 ReadyToHandshakeNtf 등 서버 푸시를 수신할 때 사용.
        /// </summary>
        public async Task<TMessage> WaitForMessageAsync<TMessage>(int msgId, CancellationToken cancellationToken = default)
            where TMessage : IMessage
        {
            var tcs = new TaskCompletionSource<IMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_messageInterceptors.TryAdd(msgId, tcs))
                throw new InvalidOperationException($"Already waiting for MsgId: {msgId}");

            using var reg = cancellationToken.Register(() =>
            {
                if (_messageInterceptors.TryRemove(msgId, out var removed))
                    removed.TrySetCanceled(cancellationToken);
            });

            var result = await tcs.Task;
            return (TMessage)result;
        }

        public void Update()
        {
            if (State == ClientState.Disconnected)
                return;

            ProcessPacket();
            CheckIdlePing();
            CheckTimeSync();
        }

        private void ProcessPacket()
        {
            const int maxBatchSize = 100; // 또는 config에서
            int processed = 0;
    
            while (processed < maxBatchSize && _recvList.TryDequeue(out var packet))
            {
                _handler.Handling(packet);
                processed++;
            }
    
            if (_recvList.Count > 0)
            {
                _logger.LogDebug("Processed {Count} packets, {Remaining} remaining", 
                    processed, _recvList.Count);
            }
        }

        private void InternalSend(EndPointHeader endPointHeader, GameHeader gameHeader, IMessage message, SendOptions options = default)
        {
            Volatile.Write(ref _lastSentTicks, Stopwatch.GetTimestamp());

            var payloadSize = message.CalculateSize();
            var totalSize = EndPointHeader.SizeOf + GameHeader.SizeOf + payloadSize;
            endPointHeader.TotalLength = totalSize;

            var buffer = ArrayPool<byte>.Shared.Rent(totalSize);
            byte[]? encryptedBuffer = null;
            try
            {
                var span = buffer.AsSpan(0, totalSize);
                int pos = 0;

                MemoryMarshal.Write(span.Slice(pos), in endPointHeader);
                pos += EndPointHeader.SizeOf;

                MemoryMarshal.Write(span.Slice(pos), in gameHeader);
                pos += GameHeader.SizeOf;

                message.WriteTo(span.Slice(pos, payloadSize));

                // 선택적 암호화: SendOptions.Encrypt 요청 + 암호화 키가 활성화된 경우만 암호화
                if (options.Encrypt && _encryptionActive && _encryptAesGcm != null &&
                    TryEncryptPacket(span, out encryptedBuffer, out var encLen))
                {
                    if (!_tcpClientImplement.SendAsync(encryptedBuffer, 0, encLen))
                    {
                        _logger.LogWarning("Encrypted SendAsync failed (counter={Counter}), nonce desync likely — disconnecting", _encryptCounter);
                        _tcpClientImplement.Disconnect();
                    }
                }
                else
                {
                    if (!_tcpClientImplement.SendAsync(buffer, 0, totalSize))
                        _logger.LogWarning("SendAsync returned false - send buffer may be full");
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error writing message to client");
                _tcpClientImplement.Disconnect();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                if (encryptedBuffer != null)
                    ArrayPool<byte>.Shared.Return(encryptedBuffer);
            }
        }

        private bool TryEncryptPacket(ReadOnlySpan<byte> plainPacket, out byte[]? encryptedBuffer, out int encryptedLength)
        {
            encryptedBuffer = null;
            encryptedLength = 0;

            if (plainPacket.Length < EndPointHeader.SizeOf || _encryptAesGcm == null)
                return false;

            var header = MemoryMarshal.Read<EndPointHeader>(plainPacket);
            if (header.IsHandshake)
                return false;

            var plaintext = plainPacket[EndPointHeader.SizeOf..];

            Span<byte> nonce = stackalloc byte[EncNonceSize];
            var cnt = Interlocked.Increment(ref _encryptCounter);
            BinaryPrimitives.WriteUInt64LittleEndian(nonce, cnt);
            nonce[8] = DirectionC2S;

            var outSize = EndPointHeader.SizeOf + EncTagSize + plaintext.Length;
            encryptedBuffer = ArrayPool<byte>.Shared.Rent(outSize);
            encryptedLength = outSize;

            var outSpan = encryptedBuffer.AsSpan();

            var newHeader = new EndPointHeader
            {
                TotalLength = outSize,
                ErrorCode = header.ErrorCode,
                Flags = (byte)(header.Flags | EndPointHeader.FlagEncrypted),
                OriginalLength = header.OriginalLength
            };
            newHeader.Write(outSpan);

            var tagSpan = outSpan.Slice(EndPointHeader.SizeOf, EncTagSize);
            var ciphertextSpan = outSpan.Slice(EndPointHeader.SizeOf + EncTagSize, plaintext.Length);

            _encryptAesGcm.Encrypt(nonce, plaintext, ciphertextSpan, tagSpan, outSpan[..EndPointHeader.SizeOf]);
            return true;
        }

        private void CheckIdlePing()
        {
            var interval = _pingInterval > TimeSpan.Zero ? _pingInterval : _config.PingInterval;
            if (!_pingEnabled || interval <= TimeSpan.Zero)
                return;

            var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastSentTicks));
            if (elapsed < interval)
                return;

            try
            {
                Send(new PingReq { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send idle PingReq");
            }
        }

        private void CheckTimeSync()
        {
            if (!_timeSyncEnabled || _config.TimeSyncInterval <= TimeSpan.Zero)
                return;

            var elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastTimeSyncTicks));
            if (elapsed < _config.TimeSyncInterval)
                return;

            Volatile.Write(ref _lastTimeSyncTicks, Stopwatch.GetTimestamp());

            try
            {
                Send(new TimeSyncReq { ClientSendTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send TimeSyncReq");
            }
        }

        private void OnConnected()
        {
            Sid = _tcpClientImplement.Socket.Handle.ToInt64();
            _logger.LogInformation("TCP connected - [sid:{Sid}]. Starting handshake...", Sid);

            // TCP 연결만 완료 처리. OnConnectedHandler는 Handshake 완료 후 ConnectAsync에서 호출됨
            var tcs = Interlocked.Exchange(ref _tcpConnectTcs, null);
            tcs?.SetResult(true);
        }

        private void OnDisconnected()
        {
            _logger.LogWarning("TCP client disconnected - [sid:{Sid}], lastError={Error}", Sid, _lastSocketError);

            var tcs = Interlocked.Exchange(ref _tcpConnectTcs, null);
            if (tcs != null)
            {
                // ConnectAsync 대기 중에 disconnect 발생 — 원인 포함한 예외로 전달
                var error = _lastSocketError;
                tcs.TrySetException(new SocketException((int)error));
            }

            _rpcManager.CancelAll();
            OnDisconnectedHandler?.Invoke();
        }

        private void OnError(SocketError error)
        {
            _lastSocketError = error;
            OnErrorHandler?.Invoke(error);
            _logger.LogWarning("Socket error: {Error} [sid:{Sid}]", error, Sid);
        }

        private void OnOnReceived(byte[] buffer, long offset, long size)
        {
            try
            {
                _receiveBuffer.Write(buffer.AsSpan((int) offset, (int) size));
                var packets = _packetParser.Parse(_receiveBuffer);
                if (packets.Count == 0)
                    return;

                foreach (var packet in packets)
                {
                    // 에러 전용 응답 처리 (Payload 없음, Message == null)
                    if (packet.IsError)
                    {
                        _logger.LogWarning(
                            "Server error received: ErrorCode={ErrorCode}, RequestId={RequestId}",
                            packet.ErrorCode, packet.GameHeader.RequestId);
                        _recvList.Enqueue(packet);
                        continue;
                    }

                    // RPC 응답 처리
                    if (packet.GameHeader.RequestId > 0)
                    {
                        if (_rpcManager.TryCompleteRequest(packet.GameHeader.RequestId, packet.Message!))
                        {
                            continue;
                        }

                        // 타임아웃된 응답 로깅
                        _logger.LogWarning(
                            "Received response for unknown/expired RequestId: {RequestId}, MsgId: {MsgId}",
                            packet.GameHeader.RequestId,
                            packet.GameHeader.MsgId
                        );

                        continue;
                    }
                    
                    // 메시지 큐 크기 제한 (DoS 방어)
                    if (_recvList.Count >= _config.MaxQueueSize)
                    {
                        _logger.LogError(
                            "Message queue full ({MaxSize}), dropping packet - MsgId: {MsgId}",
                            _config.MaxQueueSize,
                            packet.GameHeader.MsgId
                        );
                        _tcpClientImplement.Disconnect();
                        return;
                    }

                    // TimeSync 응답 인터셉트 (인프라 메커니즘, 앱에 노출 불필요)
                    if (packet.GameHeader.MsgId == TimeSyncRes.MsgId && packet.Message is TimeSyncRes syncRes)
                    {
                        var clientRecvTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var rtt = clientRecvTime - syncRes.ClientSendTimestamp;
                        var timeOffset = syncRes.ServerTimestamp - (syncRes.ClientSendTimestamp + rtt / 2);

                        Volatile.Write(ref _lastRttMs, rtt);
                        Volatile.Write(ref _serverTimeOffsetMs, timeOffset);
                        continue;
                    }

                    // 메시지 인터셉터 확인 (handshake 중 서버 푸시 메시지 수신용)
                    if (_messageInterceptors.TryRemove(packet.GameHeader.MsgId, out var tcs))
                    {
                        // 첫 서버 패킷(ReadyToHandshakeNtf)에서 SequenceId 초기값 동기화
                        if (_msgSeq == 0 && packet.GameHeader.SequenceId != 0)
                            _msgSeq = packet.GameHeader.SequenceId;

                        tcs.TrySetResult(packet.Message);
                        continue;
                    }

                    _recvList.Enqueue(packet);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnReceived Exception");
                _tcpClientImplement.Disconnect();
            }
        }

        public void Dispose()
        {
            if(_disposed)
                return;
            // 자식 리소스 먼저 정리
            _tcpConnectTcs?.TrySetCanceled();
            _encryptAesGcm?.Dispose();
            _packetParser?.Dispose();
            _rpcManager?.Dispose();
            _receiveBuffer?.Dispose();

            // 부모 리소스 마지막에 정리
            _tcpClientImplement.Dispose();
            
            
            _disposed = true;
        }
    }
}