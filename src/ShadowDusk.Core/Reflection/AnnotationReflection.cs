#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>A reflected HLSL annotation (a <c>name = value</c> pair) on an effect parameter.</summary>
public sealed record AnnotationReflection
{
    /// <summary>The annotation's name.</summary>
    public required string Name  { get; init; }
    /// <summary>
    /// The annotation's value as written in the source: a string keeps its quotes
    /// (<c>"Speed"</c>) and a number is not parsed (<c>5.0</c>).
    /// </summary>
    public required string Value { get; init; }
}
