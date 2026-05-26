#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>The output of the FX9 pre-parser: stripped HLSL plus all extracted FX9 metadata.</summary>
public sealed record FxParseResult
{
    /// <summary>HLSL source with all FX9 blocks stripped, preserving line numbers for error reporting.</summary>
    public required string StrippedHlsl { get; init; }

    /// <summary>All technique blocks extracted from the source.</summary>
    public required IReadOnlyList<TechniqueInfo> Techniques { get; init; }

    /// <summary>All sampler declarations with sampler_state blocks extracted from the source.</summary>
    public required IReadOnlyList<SamplerInfo> Samplers { get; init; }

    /// <summary>Annotation blocks attached to global parameter declarations.</summary>
    public required IReadOnlyList<ParameterAnnotation> ParameterAnnotations { get; init; }
}
