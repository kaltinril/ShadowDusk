#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// A lightweight discriminated union representing either a success value of type
/// <typeparamref name="T"/> or an error of type <typeparamref name="TError"/>. ShadowDusk
/// uses this instead of exception-as-control-flow: compilation outcomes are returned, not
/// thrown.
/// </summary>
/// <typeparam name="T">The type of the success value.</typeparam>
/// <typeparam name="TError">The type of the error value.</typeparam>
public readonly struct Result<T, TError>
{
    private readonly T? _value;
    private readonly TError? _error;

    private Result(T value)    { _value = value; _error = default; IsSuccess = true; }
    private Result(TError error) { _value = default; _error = error; IsSuccess = false; }

    /// <summary>Gets a value indicating whether this result holds a success value.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets a value indicating whether this result holds an error.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// Gets the success value. Throws <see cref="InvalidOperationException"/> if this result
    /// is a failure — check <see cref="IsSuccess"/> first.
    /// </summary>
    public T Value    => IsSuccess  ? _value!  : throw new InvalidOperationException("Result is a failure.");

    /// <summary>
    /// Gets the error value. Throws <see cref="InvalidOperationException"/> if this result is
    /// a success — check <see cref="IsFailure"/> first.
    /// </summary>
    public TError Error => IsFailure ? _error! : throw new InvalidOperationException("Result is a success.");

    /// <summary>Creates a successful result wrapping <paramref name="value"/>.</summary>
    public static Result<T, TError> Ok(T value)        => new(value);

    /// <summary>Creates a failed result wrapping <paramref name="error"/>.</summary>
    public static Result<T, TError> Fail(TError error) => new(error);

    /// <summary>
    /// Chains another fallible operation onto a success value, short-circuiting on failure.
    /// </summary>
    /// <typeparam name="TNext">The success type of the next result.</typeparam>
    /// <param name="f">The function to apply to the success value.</param>
    /// <returns>
    /// The result of <paramref name="f"/> if this result is a success; otherwise the existing
    /// error propagated unchanged.
    /// </returns>
    public Result<TNext, TError> Bind<TNext>(Func<T, Result<TNext, TError>> f)
        => IsSuccess ? f(_value!) : Result<TNext, TError>.Fail(_error!);

    /// <summary>
    /// Transforms a success value, leaving a failure untouched.
    /// </summary>
    /// <typeparam name="TNext">The mapped success type.</typeparam>
    /// <param name="f">The projection to apply to the success value.</param>
    /// <returns>
    /// A success wrapping <c>f(value)</c> if this result is a success; otherwise the existing
    /// error propagated unchanged.
    /// </returns>
    public Result<TNext, TError> Map<TNext>(Func<T, TNext> f)
        => IsSuccess ? Result<TNext, TError>.Ok(f(_value!)) : Result<TNext, TError>.Fail(_error!);
}
