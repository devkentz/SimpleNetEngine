using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace SimpleNetEngine.Infrastructure.Telemetry;

public static class OpenTelemetryExtensions
{
    /// <summary>
    /// 프로젝트 공통 OpenTelemetry 설정 (Tracing + Metrics + Logging)
    /// Aspire Dashboard가 OTEL_EXPORTER_OTLP_ENDPOINT 환경변수를 자동 주입
    /// Serilog writeToProviders:true → OTel LoggerProvider → OTLP → Aspire 구조화 로그
    /// TraceSamplingRatio는 IConfiguration "Telemetry:TraceSamplingRatio"에서 읽음 (기본 0.1 = 10%)
    /// </summary>
    public static IServiceCollection AddNetworkEngineTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var traceSamplingRatio = configuration.GetValue<double?>("Telemetry:TraceSamplingRatio") ?? 0.1;

        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(traceSamplingRatio)))
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
