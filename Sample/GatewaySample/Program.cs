using SimpleNetEngine.Gateway.Extensions;
using SimpleNetEngine.Gateway.Generated;
using GatewaySample.Config;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;


namespace GatewaySample;

class Program
{
    static async Task Main(string[] args)
    {
        // UTF-8 콘솔 인코딩 설정 (한글 깨짐 방지)
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            var host = Host.CreateDefaultBuilder(args)
                .UseSerilog((context, configuration) =>
                {
                    configuration.ReadFrom.Configuration(context.Configuration);
                }, writeToProviders: true)
                .ConfigureLogging((context, logging) =>
                {
                    logging.AddOpenTelemetry(options =>
                    {
                        options.IncludeFormattedMessage = true;
                        options.IncludeScopes = true;
                        options.AddOtlpExporter();
                    });
                })
                .ConfigureServices((context, services) =>
                {
                // Config 로드
                var config = context.Configuration.GetSection("Gateway").Get<GatewayConfig>()
                             ?? throw new InvalidOperationException("Gateway config not found");

                // OpenTelemetry 설정 (리소스 이름은 Aspire가 OTEL_SERVICE_NAME 환경변수로 주입)
                services.AddOpenTelemetry()
                    .WithTracing(tracing => tracing
                        .AddSource("NetworkEngine.Merged")
                        .AddAspNetCoreInstrumentation()
                        .AddOtlpExporter())
                    .WithMetrics(metrics => metrics
                        .AddAspNetCoreInstrumentation()
                        .AddRuntimeInstrumentation()
                        .AddOtlpExporter());

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
