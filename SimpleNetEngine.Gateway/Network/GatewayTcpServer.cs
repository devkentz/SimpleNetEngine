using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NetCoreServer;
using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;
using SimpleNetEngine.Gateway.Options;
using SimpleNetEngine.Node.Network;
using SimpleNetEngine.Protocol.Utils;

namespace SimpleNetEngine.Gateway.Network;

/// <summary>
/// Gateway TCP 서버
/// 클라이언트 연결을 수락하고 세션을 생성
/// Dumb Proxy: 비즈니스 로직 없이 연결 관리만 수행
/// </summary>
public class GatewayTcpServer : TcpServer
{
    private readonly ILogger<GatewayTcpServer> _logger;
    private readonly GamePacketRouter _packetRouter;
    private readonly INodeSender _nodeSender;
    private readonly UniqueIdGenerator _idGenerator;
    private readonly long _gatewayNodeId;
    private readonly bool _encryptionEnabled;
    private readonly ECDsa? _signingKey;

    public GatewayTcpServer(
        IOptions<GatewayOptions> options,
        ILogger<GatewayTcpServer> logger,
        GamePacketRouter packetRouter,
        INodeSender nodeSender,
        UniqueIdGenerator idGenerator)
        : base(options.Value.ClientHost, DeterminePort(options.Value, logger))
    {
        _logger = logger;
        _packetRouter = packetRouter;
        _nodeSender = nodeSender;
        _idGenerator = idGenerator;
        _gatewayNodeId = options.Value.GatewayNodeId;
        _encryptionEnabled = options.Value.EnableEncryption;
        _signingKey = _encryptionEnabled ? LoadSigningKey(options.Value.SigningKeyPath) : null;

        if (!_encryptionEnabled)
            _logger.LogWarning("Encryption is DISABLED. All packets will be transmitted in plaintext (development mode)");
    }

    /// <summary>
    /// 세션별 SessionCrypto 인스턴스 생성.
    /// 암호화 비활성 시 null 반환 (ECDH 키 생성 자체 생략).
    /// </summary>
    private SessionCrypto? CreateSessionCrypto()
    {
        return _encryptionEnabled ? new SessionCrypto(_signingKey) : null;
    }

    private ECDsa? LoadSigningKey(string? pemPath)
    {
        if (string.IsNullOrEmpty(pemPath))
        {
            _logger.LogWarning("No signing key configured (SigningKeyPath). ECDH keys will NOT be signed (MITM vulnerable)");
            return null;
        }

        // 설정 경로 또는 빌드 출력 디렉토리에서 탐색
        var resolvedPath = ResolveFilePath(pemPath);
        if (resolvedPath == null)
        {
            _logger.LogWarning("Signing key not found at {Path}. ECDH keys will NOT be signed (MITM vulnerable)", pemPath);
            return null;
        }

        var pem = File.ReadAllText(resolvedPath);
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        _logger.LogInformation("ECDSA signing key loaded from {Path}", resolvedPath);
        return ecdsa;
    }

    private static string? ResolveFilePath(string configuredPath)
    {
        if (File.Exists(configuredPath))
            return configuredPath;

        // 빌드 출력 디렉토리의 certs/ 폴더 (CopyToOutputDirectory 대응)
        var fileName = Path.GetFileName(configuredPath);
        var certsDirPath = Path.Combine("certs", fileName);
        if (File.Exists(certsDirPath))
            return certsDirPath;

        return null;
    }

    /// <summary>
    /// 설정에 따라 사용할 포트를 결정합니다.
    /// </summary>
    private static int DeterminePort(GatewayOptions options, ILogger<GatewayTcpServer> logger)
    {
        if (!options.AllowDynamicPort)
        {
            return options.TcpPort;
        }

        // 동적 포트 모드: 사용 가능한 포트 찾기
        return FindAvailablePort(options.TcpPort, logger);
    }

    /// <summary>
    /// 지정된 포트부터 시작하여 사용 가능한 포트를 찾습니다.
    /// </summary>
    private static int FindAvailablePort(int preferredPort, ILogger<GatewayTcpServer> logger)
    {
        const int maxRetries = 100;
        var currentPort = preferredPort;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                // 포트 사용 가능 여부 테스트
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Any, currentPort));

                logger.LogInformation("Found available port: {Port}", currentPort);
                return currentPort;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                logger.LogDebug("Port {Port} is in use, trying next port", currentPort);
                currentPort++;
            }
        }

        throw new InvalidOperationException(
            $"Could not find an available port after {maxRetries} attempts starting from {preferredPort}");
    }

    protected override TcpSession CreateSession()
    {
        return new GatewaySession(this, _logger, _packetRouter, _nodeSender, _gatewayNodeId, _idGenerator, CreateSessionCrypto());
    }

    protected override void OnError(SocketError error)
    {
        _logger.LogError("Gateway server error: {Error}", error);
    }

    protected override void OnStarted()
    {
        _logger.LogInformation("Gateway TCP server started on {Address}:{Port}",
            Address, Port);
        _logger.LogInformation("Client connections will be forwarded to GameServers via P2P");
    }

    protected override void OnStopped()
    {
        _logger.LogInformation("Gateway TCP server stopped");
    }

    protected override void OnConnected(TcpSession session)
    {
        _logger.LogDebug("New client connection accepted: {SessionId}", session.Id);
        base.OnConnected(session);
    }

    protected override void OnDisconnected(TcpSession session)
    {
        _logger.LogDebug("Client connection closed: {SessionId}", session.Id);
        base.OnDisconnected(session);
    }
}
