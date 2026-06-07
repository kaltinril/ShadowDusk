#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.HLSL.Reflection;

/// <summary>
/// DXBC (Shader-Model-5) reflection pipeline — the D3D11 analogue of
/// <see cref="ReflectionPipeline"/>. DXBC has no SPIR-V sibling, so there is no
/// binding-slot verification step: it reflects the DXBC directly and builds the
/// effect parameter list (cbuffer variables + textures) exactly as the DXIL path
/// does, so downstream MGFX assembly is identical.
/// </summary>
public sealed class DxbcReflectionPipeline
{
    private readonly DxbcReflectionExtractor _extractor;

    /// <summary>Creates the pipeline from a DXBC reflection extractor.</summary>
    /// <param name="extractor">The DXBC reflection extractor.</param>
    public DxbcReflectionPipeline(DxbcReflectionExtractor extractor)
    {
        _extractor = extractor;
    }

    /// <summary>
    /// Reflects the DXBC blob and builds the final effect parameter list (merging in the FX
    /// annotations).
    /// </summary>
    /// <param name="dxbcBlob">A complete SM5 DXBC module.</param>
    /// <param name="fxAnnotations">FX-level parameter annotations to merge, if any.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The reflected effect on success, or a <see cref="ShaderError"/> on failure.</returns>
    public async Task<Result<ReflectedEffect, ShaderError>> ReflectAsync(
        ReadOnlyMemory<byte> dxbcBlob,
        IReadOnlyList<ParameterAnnotation>? fxAnnotations,
        CancellationToken ct = default)
    {
        Result<ReflectedEffect, ShaderError> extractResult =
            await Task.Run(() => _extractor.Extract(dxbcBlob, ct), ct).ConfigureAwait(false);

        if (extractResult.IsFailure)
            return extractResult;

        ReflectedEffect baseEffect = extractResult.Value;
        // DX matches mgfxc: a sampler does not get its own effect parameter (the
        // texture Object parameter represents the binding), so suppress them here.
        IReadOnlyList<ParameterReflection> parameters =
            ParameterListBuilder.Build(baseEffect, fxAnnotations, includeSamplerParameters: false);

        return Result<ReflectedEffect, ShaderError>.Ok(baseEffect with { Parameters = parameters });
    }
}
