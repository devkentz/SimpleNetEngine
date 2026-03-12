using SimpleNetEngine.Protocol.Packets;

namespace SimpleNetEngine.Game.Core;

/// <summary>
/// Result&lt;T&gt; 패턴 구현
/// Exception을 던지지 않고 성공/실패를 타입 안전하게 표현
///
/// 사용 예시:
/// <code>
/// Result&lt;User&gt; result = GetUser(userId);
/// if (result.IsSuccess)
/// {
///     var user = result.Value;
/// }
/// else
/// {
///     var errorCode = result.Error;
/// }
/// </code>
/// </summary>
/// <typeparam name="T">성공 시 반환할 값의 타입</typeparam>
public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly ErrorCode _error;
    private readonly string? _errorMessage;

    /// <summary>
    /// 성공 여부
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// 실패 여부
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// 성공 시 값 (실패 시 접근하면 InvalidOperationException)
    /// </summary>
    public T Value
    {
        get
        {
            if (!IsSuccess)
                throw new InvalidOperationException(
                    $"Cannot access Value on failed Result. Error: {_error}, Message: {_errorMessage}");

            return _value!;
        }
    }

    /// <summary>
    /// 실패 시 에러 코드 (성공 시 ErrorCode.None)
    /// </summary>
    public ErrorCode Error => _error;

    /// <summary>
    /// 실패 시 추가 에러 메시지 (옵션)
    /// </summary>
    public string? ErrorMessage => _errorMessage;

    private Result(T value)
    {
        IsSuccess = true;
        _value = value;
        _error = ErrorCode.None;
        _errorMessage = null;
    }

    private Result(ErrorCode error, string? errorMessage = null)
    {
        if (error == ErrorCode.None)
            throw new ArgumentException("Success state must use Ok() factory method", nameof(error));

        IsSuccess = false;
        _value = default;
        _error = error;
        _errorMessage = errorMessage;
    }

    /// <summary>
    /// 성공 Result 생성
    /// </summary>
    public static Result<T> Ok(T value) => new(value);

    /// <summary>
    /// 실패 Result 생성
    /// </summary>
    public static Result<T> Failure(ErrorCode error, string? errorMessage = null) => new(error, errorMessage);

    /// <summary>
    /// 값에서 암묵적 변환 (편의성)
    /// </summary>
    public static implicit operator Result<T>(T value) => Ok(value);

    /// <summary>
    /// ErrorCode에서 암묵적 변환 (편의성)
    /// </summary>
    public static implicit operator Result<T>(ErrorCode error) => Failure(error);

    /// <summary>
    /// Match 패턴 (Railway-Oriented Programming)
    /// </summary>
    public TResult Match<TResult>(
        Func<T, TResult> onSuccess,
        Func<ErrorCode, string?, TResult> onFailure)
    {
        return IsSuccess
            ? onSuccess(_value!)
            : onFailure(_error, _errorMessage);
    }

    /// <summary>
    /// ToString override for debugging
    /// </summary>
    public override string ToString()
    {
        return IsSuccess
            ? $"Success({_value})"
            : $"Failure({_error}: {_errorMessage ?? "No message"})";
    }
}

/// <summary>
/// Result (값 없는 성공/실패)
/// void 메서드를 위한 Result 패턴
/// </summary>
public readonly struct Result
{
    private readonly ErrorCode _error;
    private readonly string? _errorMessage;

    /// <summary>
    /// 성공 여부
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// 실패 여부
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// 실패 시 에러 코드
    /// </summary>
    public ErrorCode Error => _error;

    /// <summary>
    /// 실패 시 추가 에러 메시지
    /// </summary>
    public string? ErrorMessage => _errorMessage;

    private Result(bool success, ErrorCode error = ErrorCode.None, string? errorMessage = null)
    {
        IsSuccess = success;
        _error = error;
        _errorMessage = errorMessage;
    }

    /// <summary>
    /// 성공 Result 생성
    /// </summary>
    public static Result Ok() => new(true);

    /// <summary>
    /// 실패 Result 생성
    /// </summary>
    public static Result Failure(ErrorCode error, string? errorMessage = null)
    {
        if (error == ErrorCode.None)
            throw new ArgumentException("Error code cannot be None for failure", nameof(error));

        return new(false, error, errorMessage);
    }

    /// <summary>
    /// ErrorCode에서 암묵적 변환
    /// </summary>
    public static implicit operator Result(ErrorCode error) => Failure(error);

    /// <summary>
    /// Match 패턴
    /// </summary>
    public TResult Match<TResult>(
        Func<TResult> onSuccess,
        Func<ErrorCode, string?, TResult> onFailure)
    {
        return IsSuccess
            ? onSuccess()
            : onFailure(_error, _errorMessage);
    }

    /// <summary>
    /// ToString override for debugging
    /// </summary>
    public override string ToString()
    {
        return IsSuccess
            ? "Success"
            : $"Failure({_error}: {_errorMessage ?? "No message"})";
    }
}
