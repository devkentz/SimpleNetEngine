using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Game.Extensions;
using SimpleNetEngine.Protocol.Packets;
using Xunit;

namespace SimpleNetEngine.Tests.Game.Extensions;

public class ResultExtensionsTests
{
    [Fact]
    public void Map_OnSuccess_TransformsValue()
    {
        // Arrange
        var result = Result<int>.Ok(10);

        // Act
        var mapped = result.Map(x => x * 2);

        // Assert
        Assert.True(mapped.IsSuccess);
        Assert.Equal(20, mapped.Value);
    }

    [Fact]
    public void Map_OnFailure_PropagatesError()
    {
        // Arrange
        var result = Result<int>.Failure(ErrorCode.GameActorNotFound, "Not found");

        // Act
        var mapped = result.Map(x => x * 2);

        // Assert
        Assert.True(mapped.IsFailure);
        Assert.Equal(ErrorCode.GameActorNotFound, mapped.Error);
        Assert.Equal("Not found", mapped.ErrorMessage);
    }

    [Fact]
    public void Bind_OnSuccess_ChainsOperation()
    {
        // Arrange
        var result = Result<int>.Ok(10);

        // Act
        var bound = result.Bind(x => x > 5
            ? Result<string>.Ok($"Value is {x}")
            : Result<string>.Failure(ErrorCode.GameInvalidRequest));

        // Assert
        Assert.True(bound.IsSuccess);
        Assert.Equal("Value is 10", bound.Value);
    }

    [Fact]
    public void Bind_OnFailure_PropagatesError()
    {
        // Arrange
        var result = Result<int>.Failure(ErrorCode.GameSessionExpired);

        // Act
        var bound = result.Bind(x => Result<string>.Ok($"Value is {x}"));

        // Assert
        Assert.True(bound.IsFailure);
        Assert.Equal(ErrorCode.GameSessionExpired, bound.Error);
    }

    [Fact]
    public void Bind_SuccessThenFailure_ReturnsSecondFailure()
    {
        // Arrange
        var result = Result<int>.Ok(3);

        // Act
        var bound = result.Bind(x => x > 5
            ? Result<string>.Ok($"Value is {x}")
            : Result<string>.Failure(ErrorCode.GameInvalidRequest, "Too small"));

        // Assert
        Assert.True(bound.IsFailure);
        Assert.Equal(ErrorCode.GameInvalidRequest, bound.Error);
        Assert.Equal("Too small", bound.ErrorMessage);
    }

    [Fact]
    public async Task BindAsync_OnSuccess_ChainsAsyncOperation()
    {
        // Arrange
        var result = Result<int>.Ok(10);

        // Act
        var bound = await result.BindAsync(async x =>
        {
            await Task.Delay(1);
            return Result<string>.Ok($"Async value: {x}");
        });

        // Assert
        Assert.True(bound.IsSuccess);
        Assert.Equal("Async value: 10", bound.Value);
    }

    [Fact]
    public async Task MapAsync_OnSuccess_TransformsAsyncValue()
    {
        // Arrange
        var result = Result<int>.Ok(5);

        // Act
        var mapped = await result.MapAsync(async x =>
        {
            await Task.Delay(1);
            return x.ToString();
        });

        // Assert
        Assert.True(mapped.IsSuccess);
        Assert.Equal("5", mapped.Value);
    }

    [Fact]
    public void OnSuccess_OnSuccess_ExecutesAction()
    {
        // Arrange
        var result = Result<int>.Ok(42);
        var actionExecuted = false;
        var capturedValue = 0;

        // Act
        var returned = result.OnSuccess(x =>
        {
            actionExecuted = true;
            capturedValue = x;
        });

        // Assert
        Assert.True(actionExecuted);
        Assert.Equal(42, capturedValue);
        Assert.True(returned.IsSuccess);
        Assert.Equal(42, returned.Value);
    }

    [Fact]
    public void OnSuccess_OnFailure_DoesNotExecuteAction()
    {
        // Arrange
        var result = Result<int>.Failure(ErrorCode.GameInternalError);
        var actionExecuted = false;

        // Act
        var returned = result.OnSuccess(x => { actionExecuted = true; });

        // Assert
        Assert.False(actionExecuted);
        Assert.True(returned.IsFailure);
    }

    [Fact]
    public void OnFailure_OnFailure_ExecutesAction()
    {
        // Arrange
        var result = Result<int>.Failure(ErrorCode.GameActorNotFound, "Actor 123");
        var actionExecuted = false;
        var capturedError = ErrorCode.None;

        // Act
        var returned = result.OnFailure((error, msg) =>
        {
            actionExecuted = true;
            capturedError = error;
        });

        // Assert
        Assert.True(actionExecuted);
        Assert.Equal(ErrorCode.GameActorNotFound, capturedError);
        Assert.True(returned.IsFailure);
    }

    [Fact]
    public void Ensure_WhenPredicateTrue_ReturnsSuccess()
    {
        // Arrange
        var result = Result<int>.Ok(10);

        // Act
        var ensured = result.Ensure(x => x > 5, ErrorCode.GameInvalidRequest);

        // Assert
        Assert.True(ensured.IsSuccess);
        Assert.Equal(10, ensured.Value);
    }

    [Fact]
    public void Ensure_WhenPredicateFalse_ReturnsFailure()
    {
        // Arrange
        var result = Result<int>.Ok(3);

        // Act
        var ensured = result.Ensure(
            x => x > 5,
            ErrorCode.GameInvalidRequest,
            "Value must be greater than 5");

        // Assert
        Assert.True(ensured.IsFailure);
        Assert.Equal(ErrorCode.GameInvalidRequest, ensured.Error);
        Assert.Equal("Value must be greater than 5", ensured.ErrorMessage);
    }

    [Fact]
    public void GetValueOrDefault_OnSuccess_ReturnsValue()
    {
        // Arrange
        var result = Result<int>.Ok(42);

        // Act
        var value = result.GetValueOrDefault(-1);

        // Assert
        Assert.Equal(42, value);
    }

    [Fact]
    public void GetValueOrDefault_OnFailure_ReturnsDefaultValue()
    {
        // Arrange
        var result = Result<int>.Failure(ErrorCode.GameActorNotFound);

        // Act
        var value = result.GetValueOrDefault(-1);

        // Assert
        Assert.Equal(-1, value);
    }

    [Fact]
    public void GetValueOrThrow_OnSuccess_ReturnsValue()
    {
        // Arrange
        var result = Result<int>.Ok(42);

        // Act
        var value = result.GetValueOrThrow();

        // Assert
        Assert.Equal(42, value);
    }

    [Fact]
    public void GetValueOrThrow_OnFailure_ThrowsException()
    {
        // Arrange
        var result = Result<int>.Failure(ErrorCode.GameSessionExpired);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => result.GetValueOrThrow());
        Assert.Contains("SessionExpired", ex.Message);
    }

    [Fact]
    public void Combine_AllSuccess_ReturnsSuccess()
    {
        // Arrange
        var result1 = Result.Ok();
        var result2 = Result.Ok();
        var result3 = Result.Ok();

        // Act
        var combined = ResultExtensions.Combine(result1, result2, result3);

        // Assert
        Assert.True(combined.IsSuccess);
    }

    [Fact]
    public void Combine_OneFailure_ReturnsFirstFailure()
    {
        // Arrange
        var result1 = Result.Ok();
        var result2 = Result.Failure(ErrorCode.GameActorNotFound, "First failure");
        var result3 = Result.Failure(ErrorCode.GameSessionExpired, "Second failure");

        // Act
        var combined = ResultExtensions.Combine(result1, result2, result3);

        // Assert
        Assert.True(combined.IsFailure);
        Assert.Equal(ErrorCode.GameActorNotFound, combined.Error);
        Assert.Equal("First failure", combined.ErrorMessage);
    }

    [Fact]
    public void CombineGeneric_AllSuccess_ReturnsArray()
    {
        // Arrange
        var result1 = Result<int>.Ok(1);
        var result2 = Result<int>.Ok(2);
        var result3 = Result<int>.Ok(3);

        // Act
        var combined = ResultExtensions.Combine(result1, result2, result3);

        // Assert
        Assert.True(combined.IsSuccess);
        Assert.Equal([1, 2, 3], combined.Value);
    }

    [Fact]
    public void CombineGeneric_OneFailure_ReturnsFirstFailure()
    {
        // Arrange
        var result1 = Result<int>.Ok(1);
        var result2 = Result<int>.Failure(ErrorCode.GameInvalidRequest);
        var result3 = Result<int>.Ok(3);

        // Act
        var combined = ResultExtensions.Combine(result1, result2, result3);

        // Assert
        Assert.True(combined.IsFailure);
        Assert.Equal(ErrorCode.GameInvalidRequest, combined.Error);
    }

    [Fact]
    public void Try_OnSuccessfulAction_ReturnsSuccess()
    {
        // Act
        var result = ResultExtensions.Try(() => 42 / 2);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(21, result.Value);
    }

    [Fact]
    public void Try_OnExceptionThrown_ReturnsFailure()
    {
        // Act
        var result = ResultExtensions.Try<int>(
            () => throw new InvalidOperationException("Test error"),
            ErrorCode.GameInternalError);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.GameInternalError, result.Error);
        Assert.Contains("Test error", result.ErrorMessage);
    }

    [Fact]
    public async Task TryAsync_OnSuccessfulAction_ReturnsSuccess()
    {
        // Act
        var result = await ResultExtensions.TryAsync(async () =>
        {
            await Task.Delay(1);
            return 42;
        });

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task TryAsync_OnExceptionThrown_ReturnsFailure()
    {
        // Act
        var result = await ResultExtensions.TryAsync<int>(
            async () =>
            {
                await Task.Delay(1);
                throw new InvalidOperationException("Async error");
            },
            ErrorCode.GameInternalError);

        // Assert
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.GameInternalError, result.Error);
        Assert.Contains("Async error", result.ErrorMessage);
    }

    [Fact]
    public void Tap_ExecutesActionAndReturnsOriginalResult()
    {
        // Arrange
        var result = Result<int>.Ok(10);
        var actionExecuted = false;
        Result<int>? capturedResult = null;

        // Act
        var returned = result.Tap(r =>
        {
            actionExecuted = true;
            capturedResult = r;
        });

        // Assert
        Assert.True(actionExecuted);
        Assert.NotNull(capturedResult);
        Assert.Equal(result.Value, capturedResult.Value.Value);
        Assert.Equal(10, returned.Value);
    }
}
