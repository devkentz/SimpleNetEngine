using SimpleNetEngine.Gateway.Extensions;
using SimpleNetEngine.Gateway.Generated;
using SimpleNetEngine.Infrastructure.Telemetry;
using GatewaySample.Config;
using Serilog;


namespace GatewaySample;

class Program
{
    static async Task Main(string[] args)
    {
        // UTF-8 콘솔 인코딩 설정 (한글 깨짐 방지)
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // ThreadPool 최소 쓰레드 사전 할당 (스케줄링 + Redis 콜백 지연 방지)
        ThreadPool.SetMinThreads(Environment.ProcessorCount * 64, 1000);
        
        try
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging => logging.ClearProviders())
                .UseSerilog((context, configuration) =>
                {
                    configuration.ReadFrom.Configuration(context.Configuration);
                }, writeToProviders: true)
                .ConfigureServices((context, services) =>
                {
                // Config 로드
                var config = context.Configuration.GetSection("Gateway").Get<GatewayConfig>()
                             ?? throw new InvalidOperationException("Gateway config not found");

                // OpenTelemetry (Tracing + Metrics + Logging)
                services.AddNetworkEngineTelemetry(context.Configuration);

                // Gateway 통합 등록 (빌더 패턴)
                services.AddGateway(gw =>
                {
                    gw.Configure(opt =>
                    {
                        opt.NodeGuid = config.NodeGuid;
                        opt.ServiceMeshPort = config.ServiceMeshPort;
                        opt.ClientHost = config.ClientHost;
                        opt.TcpPort = config.TcpPort;
                        opt.AllowDynamicPort = config.AllowDynamicPort;
                        opt.RedisConnectionString =  context.Configuration.GetConnectionString("redis") ?? 
                                                     config.RedisConnectionString;
                        opt.SigningKeyPath = config.SigningKeyPath;
                        opt.MaxPacketsPerSecond = 0;
                    });
                });

                // Source Generator: 앱 레벨 NodeController
                services.AddGeneratedNodeControllers();
                
                // Health Check 서비스 추가
                services.AddHealthChecks();
            }).ConfigureWebHostDefaults(webBuilder =>
            {
                // Kestrel 포트는 Aspire가 자동으로 할당하도록 ConfigureKestrel 제거
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHealthChecks("/health");
                        endpoints.MapHealthChecks("/alive");
                        endpoints.MapGet("/", () => "Gateway Health Check Server");
                    });
                });
            })
            .Build();

            await host.RunAsync();
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
