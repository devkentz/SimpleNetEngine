using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SimpleNetEngine.Infrastructure.Telemetry;

public static class OpenTelemetryExtensions
{
    /// <summary>
    /// 프로젝트 공통 OpenTelemetry 설정 추가
    /// </summary>
    public static IServiceCollection AddNetworkEngineTelemetry(this IServiceCollection services, string serviceName)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(TelemetryHelper.Source.Name)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                // .NET Aspire 등 환경에서 주입된 OTLP 엔드포인트 사용
                tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(TelemetryHelper.Meter.Name) // 커스텀 Meter 등록
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                metrics.AddOtlpExporter();
            });

            // 시스템 자원(Task, Thread) 모니터링 계측기 활성화
            TelemetryHelper.Meter.CreateObservableGauge("dotnet.threadpool.pending_work_items", 
            () => ThreadPool.PendingWorkItemCount, "items", "대기 중인 Task(작업 아이템) 개수");

            TelemetryHelper.Meter.CreateObservableGauge("dotnet.threadpool.thread_count", 
            () => ThreadPool.ThreadCount, "threads", "현재 스레드 풀 내 스레드 개수");

            // 로깅을 OpenTelemetry로 통합하여 OTLP Viewer에서 구조화된 로그를 볼 수 있도록 설정
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
