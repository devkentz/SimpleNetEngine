using FluentAssertions;
using SimpleNetEngine.Infrastructure.NetMQ;

namespace SimpleNetEngine.Tests.Game.NetMQ;

/// <summary>
/// NetMQ 성능 최적화(버퍼 풀) 유닛 테스트
/// </summary>
public class NetMQOptimizationTests
{
    [Fact(DisplayName = "NetMqArrayBufferPool: 요청한 크기 이상의 버퍼를 정상적으로 대여해줘야 함")]
    public void BufferPool_Should_Rent_Correct_Size()
    {
        // Arrange
        using var pool = new NetMqArrayBufferPool(maxArrayLength: 1024, maxArraysPerBucket: 10);
        int requestedSize = 512;

        // Act
        byte[] buffer = pool.Take(requestedSize);

        // Assert
        buffer.Should().NotBeNull();
        buffer.Length.Should().BeGreaterOrEqualTo(requestedSize);
        
        // Clean up
        pool.Return(buffer);
    }

    [Fact(DisplayName = "NetMqArrayBufferPool: Dispose된 이후에 Take 시 예외를 발생시켜야 함")]
    public void BufferPool_Should_Throw_If_Disposed()
    {
        // Arrange
        var pool = new NetMqArrayBufferPool();
        pool.Dispose();

        // Act
        Action act = () => pool.Take(100);

        // Assert
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact(DisplayName = "NetMqArrayBufferPool: Dispose된 이후에도 Return 호출은 안전해야 함")]
    public void BufferPool_Should_Handle_Return_After_Dispose()
    {
        // Arrange
        var pool = new NetMqArrayBufferPool();
        byte[] buffer = pool.Take(100);
        pool.Dispose();

        // Act
        Action act = () => pool.Return(buffer);

        // Assert
        act.Should().NotThrow();
    }
}
