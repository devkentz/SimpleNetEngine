using SimpleNetEngine.Game.Core;
using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Game.Extensions;

/// <summary>
/// Result&lt;T&gt; Extension Methods
/// Railway-Oriented Programming 스타일의 Functional Combinators
/// </summary>
public static class ResultExtensions
{
    /// <summary>
    /// Map: 성공 값을 변환 (functor)
    /// 실패는 그대로 전파
    ///
    /// 예시: result.Map(user => user.Name)
    /// </summary>
    public static Result<TOut> Map<TIn, TOut>(
        this Result<TIn> result,
        Func<TIn, TOut> mapper)
    {
        return result.IsSuccess
            ? Result<TOut>.Ok(mapper(result.Value))
            : Result<TOut>.Failure(result.Error, result.ErrorMessage);
    }

    /// <summary>
    /// Bind: 성공 값을 다른 Result로 변환 (monad)
    /// Chaining operations that can fail
    ///
    /// 예시: result.Bind(user => GetUserProfile(user.Id))
    /// </summary>
    public static Result<TOut> Bind<TIn, TOut>(
        this Result<TIn> result,
        Func<TIn, Result<TOut>> binder)
    {
        return result.IsSuccess
            ? binder(result.Value)
            : Result<TOut>.Failure(result.Error, result.ErrorMessage);
    }

    /// <summary>
    /// Async Bind: 비동기 Result 체이닝
    /// </summary>
    public static async Task<Result<TOut>> BindAsync<TIn, TOut>(
        this Result<TIn> result,
        Func<TIn, Task<Result<TOut>>> binder)
    {
        return result.IsSuccess
            ? await binder(result.Value)
            : Result<TOut>.Failure(result.Error, result.ErrorMessage);
    }

    /// <summary>
    /// Async Map: 비동기 변환
    /// </summary>
    public static async Task<Result<TOut>> MapAsync<TIn, TOut>(
        this Result<TIn> result,
        Func<TIn, Task<TOut>> mapper)
    {
        return result.IsSuccess
            ? Result<TOut>.Ok(await mapper(result.Value))
            : Result<TOut>.Failure(result.Error, result.ErrorMessage);
    }

    /// <summary>
    /// OnSuccess: 성공 시 side effect 실행
    /// Result는 변경하지 않고 그대로 반환
    /// </summary>
    public static Result<T> OnSuccess<T>(
        this Result<T> result,
        Action<T> action)
    {
        if (result.IsSuccess)
        {
            action(result.Value);
        }

        return result;
    }

    /// <summary>
    /// OnFailure: 실패 시 side effect 실행
    /// </summary>
    public static Result<T> OnFailure<T>(
        this Result<T> result,
        Action<ErrorCode, string?> action)
    {
        if (result.IsFailure)
        {
            action(result.Error, result.ErrorMessage);
        }

        return result;
    }

    /// <summary>
    /// Ensure: 조건 검증 (validation)
    /// 조건이 거짓이면 실패로 변환
    ///
    /// 예시: result.Ensure(user => user.IsActive, ErrorCode.Unauthorized, "User is inactive")
    /// </summary>
    public static Result<T> Ensure<T>(
        this Result<T> result,
        Func<T, bool> predicate,
        ErrorCode errorCode,
        string? errorMessage = null)
    {
        if (result.IsFailure)
            return result;

        return predicate(result.Value)
            ? result
            : Result<T>.Failure(errorCode, errorMessage);
    }

    /// <summary>
    /// GetValueOrDefault: 실패 시 기본값 반환
    /// </summary>
    public static T GetValueOrDefault<T>(this Result<T> result, T defaultValue = default!)
    {
        return result.IsSuccess ? result.Value : defaultValue;
    }

    /// <summary>
    /// GetValueOrThrow: 실패 시 예외 발생
    /// Result 패턴과 Exception 패턴 간 bridge
    /// </summary>
    public static T GetValueOrThrow<T>(this Result<T> result)
    {
        if (result.IsSuccess)
            return result.Value;

        throw new InvalidOperationException(
            $"Result failed with error: {result.Error}, Message: {result.ErrorMessage}");
    }

    /// <summary>
    /// Combine: 여러 Result를 하나로 결합
    /// 모두 성공하면 성공, 하나라도 실패하면 첫 번째 실패 반환
    /// </summary>
    public static Result Combine(params Result[] results)
    {
        foreach (var result in results)
        {
            if (result.IsFailure)
                return result;
        }

        return Result.Ok();
    }

    /// <summary>
    /// Combine: 여러 Result&lt;T&gt;를 배열로 결합
    /// </summary>
    public static Result<T[]> Combine<T>(params Result<T>[] results)
    {
        var values = new List<T>(results.Length);

        foreach (var result in results)
        {
            if (result.IsFailure)
                return Result<T[]>.Failure(result.Error, result.ErrorMessage);

            values.Add(result.Value);
        }

        return Result<T[]>.Ok(values.ToArray());
    }

    /// <summary>
    /// Try: Exception을 Result로 변환
    /// </summary>
    public static Result<T> Try<T>(Func<T> action, ErrorCode errorCode = ErrorCode.GameInternalError)
    {
        try
        {
            return Result<T>.Ok(action());
        }
        catch (Exception ex)
        {
            return Result<T>.Failure(errorCode, ex.Message);
        }
    }

    /// <summary>
    /// TryAsync: 비동기 Exception을 Result로 변환
    /// </summary>
    public static async Task<Result<T>> TryAsync<T>(
        Func<Task<T>> action,
        ErrorCode errorCode = ErrorCode.GameInternalError)
    {
        try
        {
            var value = await action();
            return Result<T>.Ok(value);
        }
        catch (Exception ex)
        {
            return Result<T>.Failure(errorCode, ex.Message);
        }
    }

    /// <summary>
    /// Tap: 성공/실패 무관하게 side effect 실행 (디버깅용)
    /// </summary>
    public static Result<T> Tap<T>(
        this Result<T> result,
        Action<Result<T>> action)
    {
        action(result);
        return result;
    }
}
