using System.Diagnostics;
using System.Diagnostics.Metrics;
using SimpleNetEngine.Protocol.Packets;
using OpenTelemetry.Context.Propagation;

namespace SimpleNetEngine.Infrastructure.Telemetry;

/// <summary>
/// OpenTelemetry 트래킹 및 메트릭을 위한 헬퍼 클래스
/// </summary>
public static class TelemetryHelper
{
    // 프로젝트 공통 ActivitySource 및 Meter
    public static readonly ActivitySource Source = new("NetworkEngine.Merged");
    public static readonly Meter Meter = new("NetworkEngine.Merged.Metrics");

    // W3C Trace Context 전파기
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    /// <summary>
    /// GSCHeader에 현재 트레이스 컨텍스트 주입
    /// </summary>
    public static void InjectContext(ref GSCHeader header)
    {
        var activity = Activity.Current;
        if (activity == null) return;

        header.TraceId = activity.TraceId.ToGuid();
        header.SpanId = activity.SpanId.ToLong();
    }

    /// <summary>
    /// NodeHeader에 현재 트레이스 컨텍스트 주입
    /// </summary>
    public static void InjectContext(ref NodeHeader header)
    {
        var activity = Activity.Current;
        if (activity == null) return;

        header.TraceId = activity.TraceId.ToGuid();
        header.SpanId = activity.SpanId.ToLong();
    }

    /// <summary>
    /// GSCHeader에서 트레이스 컨텍스트 추출 및 새 Activity 시작
    /// </summary>
    public static Activity? ExtractAndStartActivity(in GSCHeader header, string name, ActivityKind kind = ActivityKind.Internal)
    {
        if (header.TraceId == Guid.Empty)
            return Source.StartActivity(name, kind);

        var traceId = ActivityTraceId.CreateFromBytes(header.TraceId.ToByteArray());
        var spanId = ActivitySpanId.CreateFromBytes(BitConverter.GetBytes(header.SpanId));
        
        var parentContext = new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded);
        return Source.StartActivity(name, kind, parentContext);
    }

    /// <summary>
    /// NodeHeader에서 트레이스 컨텍스트 추출 및 새 Activity 시작
    /// </summary>
    public static Activity? ExtractAndStartActivity(in NodeHeader header, string name, ActivityKind kind = ActivityKind.Internal)
    {
        if (header.TraceId == Guid.Empty)
            return Source.StartActivity(name, kind);

        var traceId = ActivityTraceId.CreateFromBytes(header.TraceId.ToByteArray());
        var spanId = ActivitySpanId.CreateFromBytes(BitConverter.GetBytes(header.SpanId));
        
        var parentContext = new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded);
        return Source.StartActivity(name, kind, parentContext);
    }

    // Guid <-> ActivityTraceId 변환 확장 메서드
    private static Guid ToGuid(this ActivityTraceId traceId)
    {
        byte[] bytes = new byte[16];
        traceId.CopyTo(bytes);
        return new Guid(bytes);
    }

    // long <-> ActivitySpanId 변환 확장 메서드
    private static long ToLong(this ActivitySpanId spanId)
    {
        byte[] bytes = new byte[8];
        spanId.CopyTo(bytes);
        return BitConverter.ToInt64(bytes);
    }
}
