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

    /// <summary>
    /// The vertex <c>compile &lt;target&gt;</c> token EXACTLY as written (original case),
    /// e.g. <c>PS_SHADERMODEL</c>. <see cref="VertexProfile"/> is the lowercased form; the
    /// raw token is needed for case-sensitive C macro expansion in the recognized-profile
    /// check (SD0013). Null when the pass declares no vertex shader.
    /// </summary>
    public string? VertexProfileToken { get; init; }

    /// <summary>The pixel <c>compile &lt;target&gt;</c> token exactly as written (see
    /// <see cref="VertexProfileToken"/>). Null when the pass declares no pixel shader.</summary>
    public string? PixelProfileToken { get; init; }

    /// <summary>
    /// Source location of the vertex <c>compile &lt;target&gt;</c> profile token, used to
    /// point the recognized-profile diagnostic (SD0013) at the exact token. Null when the
    /// pass declares no vertex shader.
    /// </summary>
    public SourceSpan? VertexProfileSpan { get; init; }

    /// <summary>
    /// Source location of the pixel <c>compile &lt;target&gt;</c> profile token (SD0013
    /// diagnostic anchor). Null when the pass declares no pixel shader.
    /// </summary>
    public SourceSpan? PixelProfileSpan { get; init; }

    /// <summary>All non-shader render-state assignments in this pass.</summary>
    public required IReadOnlyList<RenderStateEntry> RenderStates { get; init; }

    /// <summary>Annotation entries attached to this pass block.</summary>
    public required IReadOnlyList<AnnotationEntry> Annotations { get; init; }
}
