using System.Diagnostics;
using Internal.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetMQ;
using NetMQ.Sockets;
using SimpleNetEngine.Protocol.Packets;
using SimpleNetEngine.Node.Config;

namespace SimpleNetEngine.Node.Network;

/// <summary>
/// 노드 간 통신을 담당하는 클래스로, NetMQ 라이브러리를 이용한 메시지 라우팅 기능을 제공합니다.
/// </summary>
public sealed class NodeCommunicator : IDisposable
{
    private readonly RouterSocket _routerSocket;
    private readonly NetMQPoller _poller;
    private readonly NetMQQueue<(byte[] nid, NodePacket packet)> _sendQueue = new NetMQQueue<(byte[] nid, NodePacket packet)>();
    private readonly ILogger<NodeCommunicator> _logger;
    private readonly NodeConfig _config;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private TaskCompletionSource<NodePacket>? _handShakeCompletionSource;

    private bool _isStarted;
    private bool _disposed;

    public event Action<NodePacket>? OnProcessPacket;
    public event Action<NodeHandShakeReq>? OnJoinNode;
    public event Action<int, Exception>? OnSendFailed;

    /// <summary>
    /// NodeCommunicator 클래스의 생성자
    /// </summary>
    /// <param name="logger">로깅을 위한 ILogger 인스턴스</param>
    /// <param name="config">노드 설정</param>
    public NodeCommunicator(ILogger<NodeCommunicator> logger, IOptions<NodeConfig> config)
    {
        _logger = logger;
        _config = config.Value;
        _routerSocket = new RouterSocket();

        _poller = new NetMQPoller();
    }

    /// <summary>
    /// 지정된 주소에 연결합니다.
    /// </summary>
    /// <param name="remoteNode"></param>
    /// <param name="req"></param>
    /// <exception cref="InvalidOperationException">이미 시작된 상태에서 호출하면 예외가 발생합니다.</exception>
    public async Task<NodeHandShakeRes?> ConnectAsync(RemoteNode remoteNode, NodeHandShakeReq req) //tcp://{ip}:{port}
    {
        Debug.Assert(remoteNode.Address != null);

        // 동시 호출 방지: 즉시 획득 시도
        if (!await _connectLock.WaitAsync(0))
        {
            throw new InvalidOperationException($"Concurrent ConnectAsync call detected. Address: {remoteNode.Address}");
        }

        try
        {
            // 1. TCP 연결 시도 (비동기, 비차단)
            _routerSocket.Connect(remoteNode.Address);

            // 2. ZMQ Identity Exchange 대기
            await Task.Delay(_config.IdentityExchangeDelayMs);

            // 3. HandShake 시도
            return await SendHandShakeWithRetryAsync();
        }
        finally
        {
            _connectLock.Release();
        }

        async Task<NodeHandShakeRes?> SendHandShakeWithRetryAsync()
        {
            for (var attempt = 1; attempt <= _config.MaxHandShakeRetries; attempt++)
            {
                try
                {
                    return await SendSingleHandShakeAsync();
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "HandShake attempt {Attempt}/{MaxRetries} failed for {Address}", attempt,
                        _config.MaxHandShakeRetries, remoteNode.Address);

                    if (attempt == _config.MaxHandShakeRetries)
                    {
                        _logger.LogError("HandShake failed after {MaxRetries} attempts for {Address}",
                            _config.MaxHandShakeRetries, remoteNode.Address);

                        _routerSocket.Disconnect(remoteNode.Address);
                        throw;
                    }

                    // Exponential backoff: 100ms, 200ms, 400ms
                    await Task.Delay(100 * (1 << (attempt - 1)));
                }
            }

            return null;
        }

        async Task<NodeHandShakeRes> SendSingleHandShakeAsync()
        {
            _handShakeCompletionSource =
                new TaskCompletionSource<NodePacket>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var timeoutCts = new CancellationTokenSource(_config.HandShakeTimeoutMs);

            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _cancellationTokenSource.Token, timeoutCts.Token);

                var task = _handShakeCompletionSource.Task;
                using var packet = NodePacket.Create(0, 0, 0, 0, false, req);
                SendInternal(remoteNode.IdentityBytes, packet);

                using var result = await task
                    .WaitAsync(linkedCts.Token);

                return NodeHandShakeRes.Parser.ParseFrom(result.Payload);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"HandShake to {remoteNode.Address} timed out after {_config.HandShakeTimeoutMs}ms");
            }
        }
    }


    /// <summary>
    /// 지정된 주소와의 연결을 해제합니다.
    /// </summary>
    /// <param name="address">연결 해제할 주소</param>
    /// <exception cref="InvalidOperationException">이미 종료된 상태에서 호출하면 예외가 발생합니다.</exception>
    public void Disconnect(string address)
    {
        Debug.Assert(!string.IsNullOrEmpty(address));

        if (_disposed)
            return;

        _routerSocket.Disconnect(address);
    }

    /// <summary>
    /// 통신 서비스를 시작합니다.
    /// </summary>
    /// <param name="identity">이 노드의 식별자</param>
    /// <param name="port">바인딩할 포트 번호</param>
    /// <exception cref="InvalidOperationException">이미 시작된 상태에서 호출하면 예외가 발생합니다.</exception>
    public int Start(byte[] identity, int port)
    {
        if (_isStarted)
            throw new InvalidOperationException("NodeCommunicator is already started");

        if (_disposed)
            throw new ObjectDisposedException(nameof(NodeCommunicator));

        _routerSocket.Options.Identity = identity;
        _routerSocket.Options.RouterMandatory = true;


        // 포트 바인딩 시도 (포트가 사용 중일 경우 재시도)
        var actualPort = TryBindToPort(port);

        _poller.Add(_routerSocket);
        _poller.Add(_sendQueue);    // 송신 담당 (이벤트 트리거)
        
        _routerSocket.ReceiveReady += OnReceiveReady;
        _sendQueue.ReceiveReady += OnSendReady;

        _isStarted = true;
        _poller.RunAsync();

        _logger.LogInformation("NodeCommunicator started on port {ActualPort}", actualPort);

        return actualPort;
    }

    void OnReceiveReady(object? _, NetMQSocketEventArgs e)
    {
        var msgIdentity = new Msg();
        var msgBody = new Msg();

        try
        {
            msgIdentity.InitEmpty();
            e.Socket.Receive(ref msgIdentity);

            msgBody.InitEmpty();
            e.Socket.Receive(ref msgBody);

            var nodePacket = NodePacket.Create(ref msgBody);
            var msgId = nodePacket.Header.MsgId;

            switch (msgId)
            {
                case NodeHandShakeRes.MsgId:
                    _handShakeCompletionSource?.TrySetResult(nodePacket);
                    break;
                case NodeHandShakeReq.MsgId:
                    OnNodeJoinPacket(nodePacket);
                    break;
                default:
                    OnProcessPacket?.Invoke(nodePacket);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Message receive failed");

            // msgBody가 초기화된 상태인지 확인 후 닫기
            if (msgBody.IsInitialised) 
                msgBody.Close();
        }
        finally
        {
            // msgIdentity는 항상 닫아줌
            if (msgIdentity.IsInitialised)
                msgIdentity.Close();
        }
    }

    private void OnNodeJoinPacket(NodePacket nodePacket)
    {
        using (nodePacket)
        {
            var packet = NodeHandShakeReq.Parser.ParseFrom(nodePacket.Payload);
            OnJoinNode?.Invoke(packet);

            Send(packet.Info.IdentityBytes.ToByteArray(), 
                NodePacket.Create(0, 0, 0, 0, false, new NodeHandShakeRes()));
        }
    }

    /// <summary>
    /// 지정된 포트에 바인딩을 시도합니다. 실패할 경우 다른 포트를 자동으로 찾습니다.
    /// </summary>
    /// <param name="preferredPort">선호하는 포트 번호</param>
    /// <returns>실제로 바인딩된 포트 번호</returns>
    private int TryBindToPort(int preferredPort)
    {
        const int maxRetries = 100;
        var currentPort = preferredPort;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                _routerSocket.Bind($"tcp://*:{currentPort}");
                return currentPort;
            }
            catch (AddressAlreadyInUseException)
            {
                _logger.LogDebug("Port {Port} is in use, trying next port", currentPort);
                currentPort++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to bind to port {Port}", currentPort);
                throw;
            }
        }

        throw new InvalidOperationException(
            $"Could not find an available port after {maxRetries} attempts starting from {preferredPort}");
    }


    /// <summary>
    /// 패킷을 지정된 노드에게 전송합니다. 비동기적으로 처리됩니다.
    /// </summary>
    /// <param name="identity">대상 노드의 식별자</param>
    /// <param name="packet">전송할 패킷</param>
    /// <exception cref="InvalidOperationException">NodeCommunicator가 시작되지 않았거나 중지된 상태인 경우 발생</exception>
    public void Send(byte[] identity, NodePacket packet)
    {
        if (!_isStarted || _disposed)
            throw new InvalidOperationException("Cannot send packet: NodeCommunicator is not started or is stopping");
 
        _sendQueue.Enqueue((identity, packet));
    }
    
    
    private void OnSendReady(object? sender, NetMQQueueEventArgs<(byte[] nid, NodePacket packet)> e)
    {
        // 큐에서 꺼내서 보냄
        while (e.Queue.TryDequeue(out var item, TimeSpan.Zero))
        {
            try 
            {
                SendInternal(item.nid, item.packet); 
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in send task");
            }
        }
    }

    /// <summary>
    /// 패킷을 내부적으로 전송합니다.
    /// </summary>
    /// <param name="identity">대상 노드의 식별자</param>
    /// <param name="packet">전송할 패킷</param>
    private void SendInternal(byte[] identity, NodePacket packet)
    {
        var identityMsg = new Msg();
        identityMsg.InitGC(identity, identity.Length);

        try
        {
            _routerSocket.Send(ref identityMsg, true);
            _routerSocket.Send(ref packet.Msg, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send packet to {Identity}", Convert.ToBase64String(identity));

            if (packet.Header.RequestKey != 0)
            {
                OnSendFailed?.Invoke(packet.Header.RequestKey, ex);
            }
        }
        finally
        {
            if (identityMsg.IsInitialised)
                identityMsg.Close();

        }
    }

    /// <summary>
    /// 통신 서비스를 중지합니다.
    /// </summary>
    public void Stop()
    {
        if (!_isStarted || _disposed)
            return;

        _logger.LogInformation("Stopping NodeCommunicator");

        try
        {
            // 채널 완료 및 취소 토큰 발행
            //_channel.Writer.Complete();
            _cancellationTokenSource.Cancel();

            // 소켓 및 관련 리소스 종료
            _poller.StopAsync();

            _isStarted = false;
            _logger.LogInformation("NodeCommunicator stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while stopping NodeCommunicator");
        }
    }

    /// <summary>
    /// 리소스를 해제합니다.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
    }

    /// <summary>
    /// 리소스를 해제하는 내부 메서드입니다.
    /// </summary>
    /// <param name="disposing">명시적 호출인 경우 true, 소멸자에서 호출되는 경우 false</param>
    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // 명시적 호출일 경우, 관리되는 리소스 해제
            try
            {
                if (_isStarted)
                    Stop();

                _poller.Dispose();
                _routerSocket.Dispose();
                _sendQueue.Dispose();
                _cancellationTokenSource.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during NodeCommunicator disposal");
            }

            _logger.LogDebug("NodeCommunicator Disposed");
        }

        _disposed = true;
    }
}