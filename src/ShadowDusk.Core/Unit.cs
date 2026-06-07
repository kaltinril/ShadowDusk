#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// A type with exactly one value, used as the success type of a
/// <see cref="Result{T, TError}"/> when an operation has no meaningful return value but can
/// still fail (the functional equivalent of <c>void</c>).
/// </summary>
public readonly struct Unit
{
    /// <summary>The single <see cref="Unit"/> value.</summary>
    public static readonly Unit Value = default;
}
