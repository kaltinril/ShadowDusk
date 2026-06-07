#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// A flattened effect parameter as exposed to the MonoGame runtime (the user-settable
/// surface, e.g. via <c>Effect.Parameters["Foo"]</c>), with its name, optional semantic,
/// type shape, and annotations.
/// </summary>
public sealed record ParameterReflection
{
    /// <summary>The parameter's name.</summary>
    public required string               Name           { get; init; }
    /// <summary>The HLSL semantic attached to the parameter, if any.</summary>
    public string?                       Semantic       { get; init; }
    /// <summary>The parameter's shape class (scalar, vector, matrix, object, …).</summary>
    public required EffectParameterClass Class          { get; init; }
    /// <summary>The parameter's element type.</summary>
    public required EffectParameterType  Type           { get; init; }
    /// <summary>The number of rows; 1 for scalars.</summary>
    public required int                  Rows           { get; init; }
    /// <summary>The number of columns; 1 for scalars.</summary>
    public required int                  Columns        { get; init; }
    /// <summary>The array element count; 1 when not an array.</summary>
    public required int                  Elements       { get; init; }
    /// <summary>The annotations declared on the parameter, if any.</summary>
    public IReadOnlyList<AnnotationReflection>? Annotations { get; init; }
}
