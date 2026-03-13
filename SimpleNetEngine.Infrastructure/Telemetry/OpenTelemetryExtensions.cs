using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace SimpleNetEngine.Infrastructure.Telemetry;

public static class OpenTelemetryExtensions
{
    /// <summary>
    /// 프로젝트 공통 OpenTelemetry 설정 (Tracing + Metrics + Logging)
    /// 리소스 이름은 Aspire가 OTEL_SERVICE_NAME 환경변수로 주입
    /// </summary>
    public static IServiceCollection AddNetworkEngineTelemetry(this IServiceCollection services)
    {
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddSource(TelemetryHelper.Source.Name)
                .AddAspNetCoreInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddMeter(TelemetryHelper.Meter.Name)
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());

        services.AddLogging(logging =>
        {
            logging.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
                options.AddOtlpExporter();
            });
        });

        return services;
    }
}
