using System.Text;
using Game.Protocol;
using Proto.Sample;
using Google.Protobuf;
using Internal.Protocol;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Client;
using SimpleNetEngine.Client.Config;
using SimpleNetEngine.Client.Network;
using StackExchange.Redis;

namespace TestClient;

class Program
{
    private const string PubKeyFileName = "server_signing_key.pub.pem";

    /// <summary>
    /// Redis에서 발견한 Gateway 엔드포인트 (host:port)
    /// </summary>
    private static List<(string Host, int Port)> _gateways = [];
    private static int _roundRobinIndex;

    static async Task Main(string[] args)
    {
        Console.WriteLine("=== SimpleNetEngine Test Client ===");
        Console.WriteLine();

        var redisConn = args.Length > 0 ? args[0] : "localhost:6379";
        var registryKey = args.Length > 1 ? args[1] : "";
        var pubKeyPath = args.Length > 2 ? args[2] : FindPublicKeyPath();

        // Redis에서 Gateway 목록 조회
        await DiscoverGatewaysFromRedis(redisConn, registryKey);

        if (_gateways.Count == 0)
        {
            Console.WriteLine("No Gateways found in Redis. Falling back to 127.0.0.1:5000");
            _gateways.Add(("127.0.0.1", 5000));
        }

        Console.WriteLine($"Discovered {_gateways.Count} Gateway(s):");
        foreach (var (host, port) in _gateways)
        {
            Console.WriteLine($"  - {host}:{port}");
        }
        Console.WriteLine();

        while (true)
        {
            Console.WriteLine("Select scenario:");
            Console.WriteLine("  1. Echo (기본 연결 + 에코)");
            Console.WriteLine("  2. Node Echo (Service Mesh RPC)");
            Console.WriteLine("  3. Duplicate Login (중복 로그인)");
            Console.WriteLine("  4. Inactivity Timeout (타임아웃)");
            Console.WriteLine("  5. Reconnect (재연결)");
            Console.WriteLine("  r. Refresh Gateway list");
            Console.WriteLine("  0. Exit");
            Console.Write("> ");

            var input = Console.ReadLine()?.Trim();
            Console.WriteLine();

            switch (input)
            {
                case "1":
                    await RunEchoScenario(pubKeyPath);
                    break;
                case "2":
                    await RunNodeEchoScenario(pubKeyPath);
                    break;
                case "3":
                    await RunDuplicateLoginScenario(pubKeyPath);
                    break;
                case "4":
                    await RunInactivityTimeoutScenario(pubKeyPath);
                    break;
                case "5":
                    await RunReconnectScenario(pubKeyPath);
                    break;
                case "r":
                    await DiscoverGatewaysFromRedis(redisConn, registryKey);
                    Console.WriteLine($"Refreshed: {_gateways.Count} Gateway(s) found");
                    break;
                case "0":
                    return;
                default:
                    Console.WriteLine("Invalid selection.");
                    break;
            }

            Console.WriteLine();
        }
    }

    /// <summary>
    /// Redis 서버 레지스트리에서 Gateway 노드 목록을 조회
    /// </summary>
    static async Task DiscoverGatewaysFromRedis(string redisConnectionString, string registryKey)
    {
        try
        {
            await using var redis = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
            var db = redis.GetDatabase();

            var entries = await db.HashGetAllAsync(registryKey);

            var gateways = new List<(string Host, int Port)>();

            foreach (var entry in entries)
            {
                try
                {
                    var serverInfo = JsonParser.Default.Parse<ServerInfo>(entry.Value!);
                    if (serverInfo.Type != EServerType.Gateway)
                        continue;

                    // 클라이언트 TCP 포트는 metadata에서 조회
                    if (!serverInfo.Metadata.TryGetValue("Gateway_ClientTcpPort", out var portStr) ||
                        !int.TryParse(portStr, out var clientPort))
                    {
                        Console.WriteLine($"  Warning: Gateway {serverInfo.RemoteId} has no ClientTcpPort in metadata, skipping");
                        continue;
                    }

                    // Service Mesh address에서 호스트 추출 (tcp://host:port → host)
                    var host = ExtractHostFromAddress(serverInfo.Address);
                    gateways.Add((host, clientPort));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Warning: Failed to parse ServerInfo: {ex.Message}");
                }
            }

            _gateways = gateways;
            _roundRobinIndex = 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to connect to Redis ({redisConnectionString}): {ex.Message}");
        }
    }

    static string ExtractHostFromAddress(string address)
    {
        // "tcp://192.168.1.1:12345" → "192.168.1.1"
        var stripped = address.Replace("tcp://", "");
        var colonIdx = stripped.LastIndexOf(':');
        return colonIdx >= 0 ? stripped[..colonIdx] : stripped;
    }

    static (string Host, int Port) NextGateway()
    {
        var gw = _gateways[_roundRobinIndex % _gateways.Count];
        _roundRobinIndex++;
        Console.WriteLine($"[RoundRobin] → Gateway {gw.Host}:{gw.Port} (index={(_roundRobinIndex - 1) % _gateways.Count})");
        return gw;
    }

    // ──────────────────────────────────────────────
    // Scenario 1: Echo (기본)
    // ──────────────────────────────────────────────
    static async Task RunEchoScenario(string? pubKeyPath)
    {
        Console.WriteLine("── Scenario: Echo ──");

        using var cts = new CancellationTokenSource();
        using var client = CreateClient(pubKeyPath);
        _ = UpdateLoop(client, cts);

        try
        {
            await ConnectAndLogin(client, "user-echo", cts.Token);

            Console.WriteLine("[Echo] Sending EchoReq...");
            var echoRes = await client.RequestAsync<EchoReq, EchoRes>(
                new EchoReq { Message = "Hello NetworkEngine!" },
                SendOptions.Encrypted, cts.Token);

            Console.WriteLine($"[Echo] Response: {echoRes.Message} (TS: {echoRes.Timestamp})");

            await SendLogout(client, cts.Token);
            Console.WriteLine("[Echo] SUCCESS");
        }
        catch (Exception ex) { PrintError(ex); }
        finally { await cts.CancelAsync(); }
    }

    // ──────────────────────────────────────────────
    // Scenario 2: Node Echo (Service Mesh RPC)
    // ──────────────────────────────────────────────
    static async Task RunNodeEchoScenario(string? pubKeyPath)
    {
        Console.WriteLine("── Scenario: Node Echo (Service Mesh RPC) ──");
        Console.WriteLine("[NodeEcho] Client → Gateway → GameServer → NodeSample (Stateless Service) → 응답");

        using var cts = new CancellationTokenSource();
        using var client = CreateClient(pubKeyPath);
        _ = UpdateLoop(client, cts);

        try
        {
            await ConnectAndLogin(client, "user-node-echo", cts.Token);

            Console.WriteLine("[NodeEcho] Sending NodeEchoReq...");
            var res = await client.RequestAsync<NodeEchoReq, NodeEchoRes>(
                new NodeEchoReq { Message = "Hello from Client via Service Mesh!" },
                SendOptions.Encrypted, cts.Token);

            Console.WriteLine($"[NodeEcho] Response: {res.Message} (TS: {res.Timestamp})");

            await SendLogout(client, cts.Token);
            Console.WriteLine("[NodeEcho] SUCCESS");
        }
        catch (Exception ex) { PrintError(ex); }
        finally { await cts.CancelAsync(); }
    }

    // ──────────────────────────────────────────────
    // Scenario 3: Duplicate Login (중복 로그인)
    // ──────────────────────────────────────────────
    static async Task RunDuplicateLoginScenario(string? pubKeyPath)
    {
        Console.WriteLine("── Scenario: Duplicate Login ──");
        Console.WriteLine("[DupLogin] 동일 credential로 두 클라이언트가 순차 로그인합니다.");
        Console.WriteLine("[DupLogin] 두 번째 로그인 성공 시 첫 번째 클라이언트는 kick-out됩니다.");

        var credential = "user-dup-login";
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        // Client 1: 먼저 로그인
        using var client1 = CreateClient(pubKeyPath);
        _ = UpdateLoop(client1, cts1);

        var client1Disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client1.OnDisconnectedHandler += () => client1Disconnected.TrySetResult();

        try
        {
            var loginRes1 = await ConnectAndLogin(client1, credential, cts1.Token);
            Console.WriteLine($"[DupLogin] Client1 logged in: ReconnectKey={loginRes1.ReconnectKey}");

            // Echo로 Client1이 Active 상태인지 확인
            var echo1 = await client1.RequestAsync<EchoReq, EchoRes>(
                new EchoReq { Message = "from client1" },
                SendOptions.Encrypted, cts1.Token);
            Console.WriteLine($"[DupLogin] Client1 echo OK: {echo1.Message}");

            // Client 2: 같은 credential로 로그인 → Client1 kick-out 예상
            Console.WriteLine("[DupLogin] Client2 connecting with same credential...");
            using var client2 = CreateClient(pubKeyPath);
            _ = UpdateLoop(client2, cts2);

            var loginRes2 = await ConnectAndLogin(client2, credential, cts2.Token);
            Console.WriteLine($"[DupLogin] Client2 logged in: ReconnectKey={loginRes2.ReconnectKey}");

            // Client1 disconnect 대기 (kick-out)
            Console.WriteLine("[DupLogin] Waiting for Client1 disconnect (kick-out)...");
            var disconnectTask = client1Disconnected.Task;
            var completed = await Task.WhenAny(disconnectTask, Task.Delay(5000));

            if (completed == disconnectTask)
            {
                Console.WriteLine("[DupLogin] Client1 disconnected (kicked out).");
            }
            else
            {
                Console.WriteLine("[DupLogin] WARNING: Client1 did not disconnect within 5s.");
            }

            // Client2 echo로 정상 동작 확인
            var echo2 = await client2.RequestAsync<EchoReq, EchoRes>(
                new EchoReq { Message = "from client2" },
                SendOptions.Encrypted, cts2.Token);
            Console.WriteLine($"[DupLogin] Client2 echo OK: {echo2.Message}");

            await SendLogout(client2, cts2.Token);
            Console.WriteLine("[DupLogin] SUCCESS");
        }
        catch (Exception ex) { PrintError(ex); }
        finally
        {
            await cts1.CancelAsync();
            await cts2.CancelAsync();
        }
    }

    // ──────────────────────────────────────────────
    // Scenario 3: Duplicate Login (중복 로그인)
    // ──────────────────────────────────────────────
    static async Task RunInactivityTimeoutScenario(string? pubKeyPath)
    {
        Console.WriteLine("── Scenario: Inactivity Timeout ──");
        Console.WriteLine("[Timeout] 로그인 후 idle ping을 비활성화하고 서버 타임아웃을 기다립니다.");
        Console.WriteLine("[Timeout] 서버 InactivityTimeout(기본 30s) + 스캔 주기 대기");

        using var cts = new CancellationTokenSource();

        // PingInterval=0으로 idle ping 비활성화
        using var client = CreateClient(pubKeyPath, pingInterval: TimeSpan.Zero);
        _ = UpdateLoop(client, cts);

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnDisconnectedHandler += () => disconnected.TrySetResult();

        try
        {
            await ConnectAndLogin(client, "user-timeout", cts.Token);
            Console.WriteLine("[Timeout] Logged in. Idle ping DISABLED. Now waiting for server timeout...");
            Console.WriteLine("[Timeout] (서버 InactivityTimeout 기본값: 30s, 스캔 주기: ~10s)");

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 최대 60초 대기 (30s timeout + 10s scan interval + margin)
            var completed = await Task.WhenAny(disconnected.Task, Task.Delay(60_000, cts.Token));

            sw.Stop();

            if (completed == disconnected.Task)
            {
                Console.WriteLine($"[Timeout] Disconnected after {sw.Elapsed.TotalSeconds:F1}s (server inactivity timeout).");
                Console.WriteLine("[Timeout] SUCCESS");
            }
            else
            {
                Console.WriteLine($"[Timeout] WARNING: Not disconnected after {sw.Elapsed.TotalSeconds:F1}s.");
                Console.WriteLine("[Timeout] Server InactivityTimeout may be disabled or set too high.");
            }
        }
        catch (Exception ex) { PrintError(ex); }
        finally { await cts.CancelAsync(); }
    }

    // ──────────────────────────────────────────────
    // Scenario 4: Inactivity Timeout (타임아웃)
    // ──────────────────────────────────────────────
    static async Task RunReconnectScenario(string? pubKeyPath)
    {
        Console.WriteLine("── Scenario: Reconnect ──");
        Console.WriteLine("[Reconnect] 로그인 → 강제 Disconnect → 재연결(ReconnectKey 사용)");

        using var cts = new CancellationTokenSource();
        using var client1 = CreateClient(pubKeyPath);
        _ = UpdateLoop(client1, cts);

        string? reconnectKey;

        try
        {
            var loginRes = await ConnectAndLogin(client1, "user-reconnect", cts.Token);
            reconnectKey = loginRes.ReconnectKey;
            Console.WriteLine($"[Reconnect] Logged in: ReconnectKey={reconnectKey}");

            // Echo 확인
            var echo1 = await client1.RequestAsync<EchoReq, EchoRes>(
                new EchoReq { Message = "before disconnect" },
                SendOptions.Encrypted, cts.Token);
            Console.WriteLine($"[Reconnect] Echo OK: {echo1.Message}");

            // 강제 disconnect (TCP 끊기)
            Console.WriteLine("[Reconnect] Disconnecting TCP...");
            client1.Disconnect();
            await Task.Delay(1000); // 서버 감지 대기
        }
        catch (Exception ex)
        {
            PrintError(ex);
            return;
        }
        finally { await cts.CancelAsync(); }

        // 재연결: 새 클라이언트로 TCP 연결 → Handshake → ReconnectReq
        Console.WriteLine("[Reconnect] Reconnecting with new TCP connection...");

        using var cts2 = new CancellationTokenSource();
        using var client2 = CreateClient(pubKeyPath);
        _ = UpdateLoop(client2, cts2);

        try
        {
            var connected = await client2.ConnectAsync(cts2.Token);
            if (!connected)
            {
                Console.WriteLine("[Reconnect] FAILED: Cannot connect for reconnect.");
                return;
            }
            Console.WriteLine("[Reconnect] TCP + Handshake OK.");

            // ReconnectReq 전송 (Cross-Node인 경우 Re-route 후 재시도)
            Console.WriteLine($"[Reconnect] Sending ReconnectReq (key={reconnectKey})...");
            var reconnRes = await client2.RequestAsync<ReconnectReq, ReconnectRes>(
                new ReconnectReq
                {
                    ReconnectKey = reconnectKey!,
                    LastSequenceId = 0
                },
                SendOptions.Encrypted, cts2.Token);

            // Cross-Node: 서버가 Gateway Re-route 후 재시도 요청
            if (!reconnRes.Success && reconnRes.RequiresRetry)
            {
                Console.WriteLine("[Reconnect] Cross-node detected, Gateway re-routed. Retrying...");
                reconnRes = await client2.RequestAsync<ReconnectReq, ReconnectRes>(
                    new ReconnectReq
                    {
                        ReconnectKey = reconnectKey!,
                        LastSequenceId = 0
                    },
                    SendOptions.Encrypted, cts2.Token);
            }

            if (reconnRes.Success)
            {
                Console.WriteLine($"[Reconnect] Reconnect OK: NewKey={reconnRes.NewReconnectKey}");
                client2.ReconnectKey = reconnRes.NewReconnectKey;

                // 재연결 후 Echo 확인
                var echo2 = await client2.RequestAsync<EchoReq, EchoRes>(
                    new EchoReq { Message = "after reconnect" },
                    SendOptions.Encrypted, cts2.Token);
                Console.WriteLine($"[Reconnect] Echo after reconnect: {echo2.Message}");

                await SendLogout(client2, cts2.Token);
                Console.WriteLine("[Reconnect] SUCCESS");
            }
            else
            {
                Console.WriteLine($"[Reconnect] FAILED: {reconnRes.ErrorMessage}");
            }
        }
        catch (Exception ex) { PrintError(ex); }
        finally { await cts2.CancelAsync(); }
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    static async Task SendLogout(NetClient client, CancellationToken ct)
    {
        try
        {
            var res = await client.RequestAsync<LogoutReq, LogoutRes>(
                new LogoutReq(), ct);
            Console.WriteLine($"  Logout: Success={res.Success}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Logout failed: {ex.Message}");
        }
    }

    static NetClient CreateClient(string? pubKeyPath, TimeSpan? pingInterval = null)
    {
        var gw = NextGateway();

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger<NetClient>();
        var handler = new MessageHandler();
        handler.Initialize();

        var config = new NetClientConfig
        {
            SigningPublicKeyPath = pubKeyPath,
            PingInterval = pingInterval ?? TimeSpan.FromSeconds(5)
        };

        return new NetClient(gw.Host, gw.Port, logger, handler, config: config);
    }

    static async Task<LoginGameRes> ConnectAndLogin(NetClient client, string credential, CancellationToken ct)
    {
        var connected = await client.ConnectAsync(ct);
        if (!connected)
            throw new Exception("Failed to connect to Gateway.");

        Console.WriteLine($"  Connected. Logging in (credential={credential})...");

        var loginRes = await client.RequestAsync<LoginGameReq, LoginGameRes>(
            new LoginGameReq { Credential = ByteString.CopyFrom(credential, Encoding.UTF8) },
            SendOptions.Encrypted, ct);

        if (!loginRes.Success)
            throw new Exception($"Login failed: {loginRes.ErrorMessage}");

        client.ReconnectKey = loginRes.ReconnectKey;
        return loginRes;
    }

    static async Task UpdateLoop(NetClient client, CancellationTokenSource cts)
    {
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                if (!client.IsDispose)
                    client.Update();

                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    static void PrintError(Exception ex)
    {
        Console.WriteLine($"  ERROR: {ex.Message}");
        if (ex.InnerException != null)
            Console.WriteLine($"  Inner: {ex.InnerException.Message}");
    }

    static string? FindPublicKeyPath()
    {
        string[] candidates =
        [
            Path.Combine("..", "certs", PubKeyFileName),
            Path.Combine("certs", PubKeyFileName),
            Path.Combine("Sample", "certs", PubKeyFileName),
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return candidates[0];
    }
}
