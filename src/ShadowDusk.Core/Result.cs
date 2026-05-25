#nullable enable

namespace ShadowDusk.Core;

public readonly struct Result<T, TError>
{
    private readonly T? _value;
    private readonly TError? _error;

    private Result(T value)    { _value = value; _error = default; IsSuccess = true; }
    private Result(TError error) { _value = default; _error = error; IsSuccess = false; }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    public T Value    => IsSuccess  ? _value!  : throw new InvalidOperationException("Result is a failure.");
    public TError Error => IsFailure ? _error! : throw new InvalidOperationException("Result is a success.");

    public static Result<T, TError> Ok(T value)        => new(value);
    public static Result<T, TError> Fail(TError error) => new(error);

    public Result<TNext, TError> Bind<TNext>(Func<T, Result<TNext, TError>> f)
        => IsSuccess ? f(_value!) : Result<TNext, TError>.Fail(_error!);

    public Result<TNext, TError> Map<TNext>(Func<T, TNext> f)
        => IsSuccess ? Result<TNext, TError>.Ok(f(_value!)) : Result<TNext, TError>.Fail(_error!);
}
