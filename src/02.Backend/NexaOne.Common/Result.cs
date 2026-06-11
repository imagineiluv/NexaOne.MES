namespace NexaOne.Common;

public sealed class Result
{
    private Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None) throw new InvalidOperationException();
        if (!isSuccess && error == Error.None) throw new InvalidOperationException();
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);
    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);
    public static Result Failure(Error error) => new(false, error);
    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

public sealed class Result<TValue>
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error)
    {
        IsSuccess = isSuccess;
        Error = error;
        _value = value;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }
    public TValue Value => IsSuccess ? _value! : throw new InvalidOperationException("Failure result has no value.");

    public static implicit operator Result<TValue>(TValue value) => Result.Success(value);
}
