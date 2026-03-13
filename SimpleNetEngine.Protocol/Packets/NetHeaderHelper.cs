using System.Runtime.CompilerServices;

namespace SimpleNetEngine.Protocol.Packets;

/// <summary>
/// INetHeader&lt;T&gt; 기반 generic 헤더 유틸리티
/// </summary>
public static class NetHeaderHelper
{
    /// <summary>
    /// 길이 검증 + 헤더 읽기 (size check + read 원샷)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryRead<T>(ReadOnlySpan<byte> span, out T header)
        where T : unmanaged, INetHeader<T>
    {
        if (span.Length < T.SizeOf)
        {
            header = default;
            return false;
        }
        header = T.Read(span);
        return true;
    }

    /// <summary>
    /// 헤더 이후 페이로드 슬라이싱
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> GetPayload<T>(ReadOnlySpan<byte> packet)
        where T : unmanaged, INetHeader<T>
        => packet[T.SizeOf..];

    /// <summary>
    /// 헤더 이후 페이로드 슬라이싱 (Span 버전)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<byte> GetPayload<T>(Span<byte> packet)
        where T : unmanaged, INetHeader<T>
        => packet[T.SizeOf..];

    /// <summary>
    /// 헤더 크기 이상인지 검증
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasHeader<T>(ReadOnlySpan<byte> span)
        where T : unmanaged, INetHeader<T>
        => span.Length >= T.SizeOf;

    /// <summary>
    /// zero-copy 헤더 참조 (복사 없이 ref readonly 반환)
    /// HasHeader로 먼저 검증한 뒤 호출할 것
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref readonly T Peek<T>(ReadOnlySpan<byte> span)
        where T : unmanaged, INetHeader<T>
        => ref T.AsRef(span);
}
