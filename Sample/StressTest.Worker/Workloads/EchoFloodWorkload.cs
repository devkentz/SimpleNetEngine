using System.Text;
using DFrame;
using Game.Protocol;
using Google.Protobuf;
using Proto.Sample;
using SimpleNetEngine.Client;
using StressTest.Worker;
using StressTest.Worker.Services;

namespace StressTest.Workloads;

/// <summary>
/// Echo 대량 요청 워크로드
/// Setup에서 Connect + Login 후, Execute마다 Echo 1회 전송
/// ClientUpdateService가 전용 쓰레드에서 일괄 Update 처리
/// </summary>
public class EchoFloodWorkload : Workload
{
    private static int _staggerIndex;
    private readonly GatewayConfig _config;
    private readonly ClientUpdateService _updateService;
    private NetClient? _client;

    public EchoFloodWorkload(GatewayConfig config, ClientUpdateService updateService)
    {
        _config = config;
        _updateService = updateService;
    }

    public override async Task SetupAsync(WorkloadContext context)
    {
        var order = Interlocked.Increment(ref _staggerIndex);
        await Task.Delay(order * 20);

        _client = _config.CreateClient();
        _updateService.Register(_client);

        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var connected = await _client.ConnectAsync(connectCts.Token, timeoutMs: 30000);
        if (!connected)
            throw new Exception($"Worker {context.WorkloadId}: Connect failed");

        var userId = $"dframe-echo-{context.WorkloadId}-{Guid.NewGuid()}";
        var loginRes = await _client.RequestAsync<LoginGameReq, LoginGameRes>(
            new LoginGameReq { Credential = ByteString.CopyFrom(userId, Encoding.UTF8) },
            SendOptions.Encrypted, connectCts.Token);

        if (!loginRes.Success)
            throw new Exception($"Worker {context.WorkloadId}: Login failed - {loginRes.ErrorMessage}");
    }

    public override async Task ExecuteAsync(WorkloadContext context)
    {
        await _client!.RequestAsync<EchoReq, EchoRes>(
            new EchoReq { Message = "dframe-flood" },
            SendOptions.Default, context.CancellationToken);
    }

    public override Task TeardownAsync(WorkloadContext context)
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }

    public override Dictionary<string, string>? Complete(WorkloadContext context)
    {
        Interlocked.Exchange(ref _staggerIndex, 0);
        _updateService.Reset();
        return base.Complete(context);
    }
}
