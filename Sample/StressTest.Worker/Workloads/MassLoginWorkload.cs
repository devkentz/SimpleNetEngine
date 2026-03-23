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
/// 대량 로그인 워크로드
/// 매 Execute마다 Connect → Login → Echo → Logout → Disconnect 전체 사이클 수행
/// </summary>
public class MassLoginWorkload : Workload
{
    private readonly GatewayConfig _config;
    private readonly ClientUpdateService _updateService;

    public MassLoginWorkload(GatewayConfig config, ClientUpdateService updateService)
    {
        _config = config;
        _updateService = updateService;
    }

    public override async Task ExecuteAsync(WorkloadContext context)
    {
        using var client = _config.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _updateService.Register(client);

        try
        {
            var connected = await client.ConnectAsync(cts.Token, 45_000);
            if (!connected)
                throw new Exception("Connect failed");

            var userId = $"dframe-login-{context.WorkloadId}-{context.ExecuteCount}";
            var loginRes = await client.RequestAsync<LoginGameReq, LoginGameRes>(
                new LoginGameReq { Credential = ByteString.CopyFrom(userId, Encoding.UTF8) },
                SendOptions.Encrypted, cts.Token);

            if (!loginRes.Success)
                throw new Exception($"Login failed: {loginRes.ErrorMessage}");

            // Echo 1회
            await client.RequestAsync<EchoReq, EchoRes>(
                new EchoReq { Message = "mass-login-test" },
                SendOptions.Encrypted, cts.Token);

            // Logout
            await client.RequestAsync<LogoutReq, LogoutRes>(
                new LogoutReq(), cts.Token);
        }
        finally
        {
            await cts.CancelAsync();
            client.Dispose();
        }
    }

    public override Dictionary<string, string>? Complete(WorkloadContext context)
    {
        _updateService.Reset();
        return base.Complete(context);
    }
}
