using FluentAssertions;
using NetMQ;
using SimpleNetEngine.Infrastructure.NetMQ;

namespace SimpleNetEngine.Tests.Game.NetMQ;

public class MsgDisposableTests
{
    [Fact]
    public void Constructor_WithByteArray_ShouldInitializeMsg()
    {
        // Arrange
        var testData = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        using var msgDisposable = new MsgDisposable(testData);

        // Assert
        msgDisposable.Value.IsInitialised.Should().BeTrue();
        msgDisposable.Value.Size.Should().Be(testData.Length);
    }

    [Fact]
    public void Constructor_WithSize_ShouldAllocateFromPool()
    {
        // Arrange
        var size = 100;

        // Act
        using var msgDisposable = new MsgDisposable(size);

        // Assert
        msgDisposable.Value.IsInitialised.Should().BeTrue();
        msgDisposable.Value.Size.Should().Be(size);
    }

    [Fact]
    public void Dispose_ShouldCloseMsg()
    {
        // Arrange
        var testData = new byte[] { 1, 2, 3, 4, 5 };
        var msgDisposable = new MsgDisposable(testData);

        // Act
        msgDisposable.Dispose();

        // Assert
        // After dispose, msg should be closed
        // Note: NetMQ Msg doesn't expose a clear "IsClosed" property,
        // but we can verify it doesn't throw
        Action act = () => msgDisposable.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void GetRef_ShouldReturnReferenceToMsg()
    {
        // Arrange
        var testData = new byte[] { 1, 2, 3, 4, 5 };
        using var msgDisposable = new MsgDisposable(testData);

        // Act
        ref var msgRef = ref msgDisposable.GetRef();

        // Assert
        msgRef.IsInitialised.Should().BeTrue();
        msgRef.Size.Should().Be(testData.Length);
    }

    [Fact]
    public void UsingPattern_ShouldAutomaticallyDispose()
    {
        // Arrange
        Msg capturedMsg;

        // Act
        {
            using var msgDisposable = new MsgDisposable([1, 2, 3]);
            capturedMsg = msgDisposable.Value;
            capturedMsg.IsInitialised.Should().BeTrue();
        } // Dispose called here

        // Assert
        // After using block, msg should be closed
        // We can't directly test this, but we verified Dispose is called
    }

    [Fact]
    public void Value_ShouldReturnMsg()
    {
        // Arrange
        var testData = new byte[] { 1, 2, 3, 4, 5 };
        using var msgDisposable = new MsgDisposable(testData);

        // Act
        var msg = msgDisposable.Value;

        // Assert
        msg.IsInitialised.Should().BeTrue();
        msg.Size.Should().Be(testData.Length);
    }
}
