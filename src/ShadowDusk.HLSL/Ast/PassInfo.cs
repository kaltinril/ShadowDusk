#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>Represents a single pass block within an FX9 technique.</summary>
public sealed record PassInfo
{
    /// <summary>The declared pass name.</summary>
    public required string Name { get; init; }

    /// <summary>Source location of the entire pass block.</summary>
    public required SourceSpan Span { get; init; }

    /// <summary>Vertex shader entry-point function name, e.g. "VSMain".</summary>
    public required string? VertexEntryPoint { get; init; }

    /// <summary>Pixel shader entry-point function name, e.g. "PSMain".</summary>
    public required string? PixelEntryPoint { get; init; }

    /// <summary>Vertex shader profile string, e.g. "vs_3_0" or "vs_5_0".</summary>
    public required string? VertexProfile { get; init; }

    /// <summary>Pixel shader profile string, e.g. "ps_3_0" or "ps_5_0".</summary>
    public required string? PixelProfile { get; init; }

    /// <summary>All non-shader render-state assignments in this pass.</summary>
    public required IReadOnlyList<RenderStateEntry> RenderStates { get; init; }

    /// <summary>Annotation entries attached to this pass block.</summary>
    public required IReadOnlyList<AnnotationEntry> Annotations { get; init; }
}
