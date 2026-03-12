using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Protocol.Packets;
using Xunit;

namespace SimpleNetEngine.Tests.Game.Core;

public class ResultTests
{
    [Fact]
    public void Value_OnFailure_ThrowsInvalidOperationException()
    {
        // Arrange
        var result = Result<int>.Failure(ErrorCode.GameInternalError);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Contains("Cannot access Value on failed Result", ex.Message);
        Assert.Contains("InternalError", ex.Message);
    }

    [Fact]
    public void ImplicitConversion_FromValue_CreatesSuccessResult()
    {
        // Arrange & Act
        Result<string> result = "Hello World";

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("Hello World", result.Value);
    }

    [Fact]
    public void ImplicitConversion_FromErrorCode_CreatesFailureResult()
    {
        // Arrange & Act
        Result<string> result = ErrorCode.GameSessionNotFound;

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.GameSessionNotFound, result.Error);
    }

    [Fact]
    public void Match_OnSuccess_CallsSuccessFunction()
    {
        // Arrange
        var result = Result<int>.Ok(10);
        var successCalled = false;
        var failureCalled = false;

        // Act
        var output = result.Match(
            value => { successCalled = true; return value * 2; },
            (error, msg) => { failureCalled = true; return -1; }
        );

        // Assert
        Assert.True(successCalled);
        Assert.False(failureCalled);
        Assert.Equal(20, output);
    }

    [Fact]
    public void Match_OnFailure_CallsFailureFunction()
    {
        // Arrange
        var result = Result<int>.Failure(ErrorCode.GameInvalidRequest, "Bad input");
        var successCalled = false;
        var failureCalled = false;

        // Act
        var output = result.Match(
            value => { successCalled = true; return value * 2; },
            (error, msg) =>
            {
                failureCalled = true;
                Assert.Equal(ErrorCode.GameInvalidRequest, error);
                Assert.Equal("Bad input", msg);
                return -1;
            }
        );

        // Assert
        Assert.False(successCalled);
        Assert.True(failureCalled);
        Assert.Equal(-1, output);
    }

}

public class ResultNoValueTests
{
    [Fact]
    public void Failure_WithNoneErrorCode_ThrowsArgumentException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => Result.Failure(ErrorCode.None));
        Assert.Contains("Error code cannot be None", ex.Message);
    }

    [Fact]
    public void ImplicitConversion_FromErrorCode_CreatesFailureResult()
    {
        // Arrange & Act
        Result result = ErrorCode.GameUnauthorized;

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.GameUnauthorized, result.Error);
    }

    [Fact]
    public void Match_OnSuccess_CallsSuccessFunction()
    {
        // Arrange
        var result = Result.Ok();
        var successCalled = false;
        var failureCalled = false;

        // Act
        var output = result.Match(
            () => { successCalled = true; return "Success"; },
            (error, msg) => { failureCalled = true; return "Failure"; }
        );

        // Assert
        Assert.True(successCalled);
        Assert.False(failureCalled);
        Assert.Equal("Success", output);
    }

    [Fact]
    public void Match_OnFailure_CallsFailureFunction()
    {
        // Arrange
        var result = Result.Failure(ErrorCode.GamePacketSendFailed);
        var successCalled = false;
        var failureCalled = false;

        // Act
        var output = result.Match(
            () => { successCalled = true; return "Success"; },
            (error, msg) =>
            {
                failureCalled = true;
                Assert.Equal(ErrorCode.GamePacketSendFailed, error);
                return "Failure";
            }
        );

        // Assert
        Assert.False(successCalled);
        Assert.True(failureCalled);
        Assert.Equal("Failure", output);
    }
}
