using GameSample.Generated;
using SimpleNetEngine.Game.Extensions;
using SimpleNetEngine.Game.Options;
using Serilog;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace GameSample;

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
                    // GameServer 설정 로드
                    var config = context.Configuration.GetSection("GameServer").Get<GameOptions>()
                        ?? throw new InvalidOperationException("GameServer config not found");

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

                    // GameServer 통합 등록 (빌더 패턴)
                    services.AddGameServer(game =>
                    {
                        game.Configure(opt =>
                        {
                            opt.NodeGuid = config.NodeGuid;
                            opt.GameSessionChannelPort = config.GameSessionChannelPort;
                            opt.AllowDynamicPort = config.AllowDynamicPort;
                            opt.ServiceMeshPort = config.ServiceMeshPort;
                            opt.RedisConnectionString =  context.Configuration.GetConnectionString("redis") ?? 
                                                         config.RedisConnectionString;
                        });
                        game.UseLoginHandler<GameLoginHandler>();
                    });

                    // Source Generator: 앱 레벨 UserController 자동 등록
                    services.AddGeneratedUserControllers();

                    // Health Check 서비스 추가
                    services.AddHealthChecks();
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    // Kestrel 포트는 Aspire가 자동으로 할당하도록 ConfigureKestrel 제거
                    webBuilder.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHealthChecks("/health");
                            endpoints.MapHealthChecks("/alive");
                            endpoints.MapGet("/", () => "GameServer Health Check Server");
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
