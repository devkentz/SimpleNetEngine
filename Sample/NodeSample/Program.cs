using NodeSample.Generated;
using SimpleNetEngine.Node.Extensions;
using SimpleNetEngine.Infrastructure.Telemetry;
using Serilog;
using NodeSample.Middleware;

namespace NodeSample;

class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

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
                    var section = context.Configuration.GetSection("NodeSample");

                    // OpenTelemetry (Tracing + Metrics + Logging)
                    services.AddNetworkEngineTelemetry(context.Configuration);

                    // Stateless Service 통합 등록 (빌더 패턴)
                    services.AddStatelessService(opt =>
                    {
                        opt.Name = section.GetValue("Name", "EchoNode")!;
                        opt.NodeGuid = section.GetValue<Guid?>("NodeGuid") ?? Guid.NewGuid();
                        opt.ServiceMeshPort = section.GetValue("ServiceMeshPort", 0);
                        opt.AllowDynamicPort = section.GetValue("AllowDynamicPort", true);
                        opt.RedisConnectionString =   context.Configuration.GetConnectionString("redis") ?? 
                                                      section.GetValue("RedisConnectionString", "redis-dev.k8s.home:6379")!;
                    });

                    // Source Generator: 앱 레벨 NodeController 핸들러 registrar 자동 등록
                    services.AddGeneratedNodeControllers();

                    services.AddNodeMiddleware<SampleNodeMiddleware>(ServiceLifetime.Scoped);
                    // Health Check
                    services.AddHealthChecks();
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHealthChecks("/health");
                            endpoints.MapHealthChecks("/alive");
                            endpoints.MapGet("/", () => "NodeSample (Stateless Service) Health Check Server");
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
