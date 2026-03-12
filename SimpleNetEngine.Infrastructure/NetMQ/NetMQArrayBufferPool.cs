using System.Buffers;
using NetMQ;

namespace SimpleNetEngine.Infrastructure.NetMQ;

/// <summary>
/// .NET ArrayPool<byte> 기반의 NetMQ 커스텀 버퍼 풀 구현체.
/// NetMQ의 기본 GCBufferPool 대신 고성능의 ArrayPool을 사용하도록 교체합니다.
/// </summary>
public sealed class NetMqArrayBufferPool : IBufferPool
{
    private ArrayPool<byte>? _pool;
    private bool _disposed;

    /// <summary>
    /// 네트워크 엔진 전용으로 독립된 고성능 풀 생성
    /// </summary>
    /// <param name="maxArrayLength">최대 배열 크기 (기본 64KB)</param>
    /// <param name="maxArraysPerBucket">버킷당 최대 배열 개수 (기본 50,000개)</param>
    public NetMqArrayBufferPool(int maxArrayLength = 1024 * 64, int maxArraysPerBucket = 50000)
    {
        _pool = ArrayPool<byte>.Create(maxArrayLength, maxArraysPerBucket);
    }

    /// <summary>
    /// 버퍼 대여 (ArrayPool.Rent)
    /// </summary>
    public byte[] Take(int size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pool!.Rent(size);
    }

    /// <summary>
    /// 버퍼 반환 (ArrayPool.Return)
    /// </summary>
    public void Return(byte[] buffer)
    {
        // Dispose 이후에도 이미 대여된 버퍼의 반환은 허용 (안정성)
        _pool?.Return(buffer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _pool = null; // 참조 해제하여 GC 대상이 되도록 함
        _disposed = true;
    }
}
