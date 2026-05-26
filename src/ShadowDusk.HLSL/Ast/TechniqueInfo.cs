#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>Represents a parsed FX9 technique block.</summary>
public sealed record TechniqueInfo
{
    /// <summary>The declared technique name.</summary>
    public required string Name { get; init; }

    /// <summary>Source location of the entire technique block.</summary>
    public required SourceSpan Span { get; init; }

    /// <summary>All passes declared inside this technique.</summary>
    public required IReadOnlyList<PassInfo> Passes { get; init; }

    /// <summary>Annotation entries attached to this technique.</summary>
    public required IReadOnlyList<AnnotationEntry> Annotations { get; init; }

    /// <summary>True if declared with the "technique11" keyword.</summary>
    public required bool IsEffect11 { get; init; }
}
