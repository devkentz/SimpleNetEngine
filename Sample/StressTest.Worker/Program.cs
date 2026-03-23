using System.Runtime.InteropServices;
using DFrame;
using Google.Protobuf;
using Internal.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimpleNetEngine.Client;
using SimpleNetEngine.Client.Config;
using SimpleNetEngine.Client.Network;
using StackExchange.Redis;
using StressTest.Worker.Services;

namespace StressTest.Worker;

class Program
{
    private const string PubKeyFileName = "server_signing_key.pub.pem";

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        ThreadPool.SetMinThreads(1000, 1000);

        PrintHardwareInfo();

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        var controllerAddress = config.GetValue("DFrame:ControllerAddress", "http://localhost:7313")!;
        var redisConn = config.GetValue("Redis:Connection", "localhost:6379")!;

        Console.WriteLine($"DFrame Worker connecting to controller: {controllerAddress}");

        var pubKeyPath = FindPublicKeyPath();
        var gateways = await DiscoverGatewaysFromRedis(redisConn);

        if (gateways.Count == 0)
        {
            var fallbackHost = config.GetValue("Gateway:FallbackHost", "127.0.0.1")!;
            var fallbackPort = config.GetValue("Gateway:FallbackPort", 5000);
            Console.WriteLine($"No Gateways found in Redis. Falling back to {fallbackHost}:{fallbackPort}");
            gateways.Add((fallbackHost, fallbackPort));
        }

        Console.WriteLine($"Discovered {gateways.Count} Gateway(s):");
        foreach (var (host, port) in gateways)
            Console.WriteLine($"  - {host}:{port}");

        var builder = DFrameApp.CreateBuilder(0, 0);
        builder.ConfigureWorker(options =>
        {
            options.ControllerAddress = controllerAddress;
        });
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(new GatewayConfig(gateways, pubKeyPath));
            services.AddSingleton<ClientUpdateService>();
        });

        await builder.RunWorkerAsync();
    }

    static async Task<List<(string Host, int Port)>> DiscoverGatewaysFromRedis(string redisConnectionString)
    {
        var gateways = new List<(string Host, int Port)>();
        try
        {
            using var redis = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
            var db = redis.GetDatabase();
            var entries = await db.HashGetAllAsync("");

            foreach (var entry in entries)
            {
                try
                {
                    var serverInfo = JsonParser.Default.Parse<ServerInfo>(entry.Value!);
                    if (serverInfo.Type != EServerType.Gateway) continue;

                    if (!serverInfo.Metadata.TryGetValue("Gateway_ClientTcpPort", out var portStr) ||
                        !int.TryParse(portStr, out var clientPort))
                        continue;

                    var host = ExtractHostFromAddress(serverInfo.Address);
                    gateways.Add((host, clientPort));
                }
                catch { /* skip malformed entries */ }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to connect to Redis ({redisConnectionString}): {ex.Message}");
        }

        return gateways;
    }

    static string ExtractHostFromAddress(string address)
    {
        var stripped = address.Replace("tcp://", "");
        var colonIdx = stripped.LastIndexOf(':');
        return colonIdx >= 0 ? stripped[..colonIdx] : stripped;
    }

    static string? FindPublicKeyPath()
    {
        string[] candidates =
        [
            Path.Combine("..", "certs", PubKeyFileName),
            Path.Combine("certs", PubKeyFileName),
            Path.Combine("Sample", "certs", PubKeyFileName),
            Path.Combine("/app/certs", PubKeyFileName),
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return candidates[0];
    }

    static void PrintHardwareInfo()
    {
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  Hardware Info");
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine($"  OS          : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"  Arch        : {RuntimeInformation.OSArchitecture}");
        Console.WriteLine($"  Runtime     : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"  Processors  : {Environment.ProcessorCount}");
        Console.WriteLine($"  Machine     : {Environment.MachineName}");

        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            Console.WriteLine($"  Total Memory: {gcInfo.TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024):F1} GB");
        }
        catch { /* GC info unavailable */ }

        Console.WriteLine("═══════════════════════════════════════════");
    }
}

/// <summary>
/// Gateway 연결 정보 (DI로 Workload에 주입)
/// </summary>
public record GatewayConfig(List<(string Host, int Port)> Gateways, string? PubKeyPath)
{
    private int _gwIndex;

    public (string Host, int Port) NextGateway()
    {
        var idx = Interlocked.Increment(ref _gwIndex) % Gateways.Count;
        return Gateways[idx];
    }

    public NetClient CreateClient()
    {
        var gw = NextGateway();
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        var logger = loggerFactory.CreateLogger<NetClient>();
        var handler = new MessageHandler();
        handler.Initialize();

        var config = new NetClientConfig
        {
            SigningPublicKeyPath = PubKeyPath,
            PingInterval = TimeSpan.FromSeconds(10)
        };

        return new NetClient(gw.Host, gw.Port, logger, handler, config: config);
    }
}
