#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>Annotation block attached to a global parameter declaration.</summary>
public sealed record ParameterAnnotation
{
    /// <summary>The name of the global parameter this annotation is attached to.</summary>
    public required string ParameterName { get; init; }

    /// <summary>All entries declared inside the annotation angle-bracket block.</summary>
    public required IReadOnlyList<AnnotationEntry> Entries { get; init; }

    /// <summary>Source location of the entire annotation block.</summary>
    public required SourceSpan Span { get; init; }
}
