using System.Buffers.Binary;
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

        WriteTraceId(activity.TraceId, out header.TraceIdHigh, out header.TraceIdLow);
        header.SpanId = SpanIdToLong(activity.SpanId);
    }

    /// <summary>
    /// NodeHeader에 현재 트레이스 컨텍스트 주입
    /// </summary>
    public static void InjectContext(ref NodeHeader header)
    {
        var activity = Activity.Current;
        if (activity == null) return;

        WriteTraceId(activity.TraceId, out header.TraceIdHigh, out header.TraceIdLow);
        header.SpanId = SpanIdToLong(activity.SpanId);
    }

    /// <summary>
    /// GSCHeader에서 트레이스 컨텍스트 추출 및 새 Activity 시작
    /// </summary>
    public static Activity? ExtractAndStartActivity(in GSCHeader header, string name, ActivityKind kind = ActivityKind.Internal)
    {
        if (header.TraceIdHigh == 0 && header.TraceIdLow == 0)
            return Source.StartActivity(name, kind);

        var traceId = ReadTraceId(header.TraceIdHigh, header.TraceIdLow);
        var spanId = LongToSpanId(header.SpanId);

        var parentContext = new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded);
        return Source.StartActivity(name, kind, parentContext);
    }

    /// <summary>
    /// NodeHeader에서 트레이스 컨텍스트 추출 및 새 Activity 시작
    /// </summary>
    public static Activity? ExtractAndStartActivity(in NodeHeader header, string name, ActivityKind kind = ActivityKind.Internal)
    {
        if (header.TraceIdHigh == 0 && header.TraceIdLow == 0)
            return Source.StartActivity(name, kind);

        var traceId = ReadTraceId(header.TraceIdHigh, header.TraceIdLow);
        var spanId = LongToSpanId(header.SpanId);

        var parentContext = new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded);
        return Source.StartActivity(name, kind, parentContext);
    }

    // ActivityTraceId → (high, low) 변환 (zero-alloc)
    private static void WriteTraceId(ActivityTraceId traceId, out long high, out long low)
    {
        Span<byte> bytes = stackalloc byte[16];
        traceId.CopyTo(bytes);
        high = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        low = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
    }

    // (high, low) → ActivityTraceId 변환 (zero-alloc)
    private static ActivityTraceId ReadTraceId(long high, long low)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, high);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], low);
        return ActivityTraceId.CreateFromBytes(bytes);
    }

    // ActivitySpanId → long 변환
    private static long SpanIdToLong(ActivitySpanId spanId)
    {
        Span<byte> bytes = stackalloc byte[8];
        spanId.CopyTo(bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    // long → ActivitySpanId 변환
    private static ActivitySpanId LongToSpanId(long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return ActivitySpanId.CreateFromBytes(bytes);
    }
}
