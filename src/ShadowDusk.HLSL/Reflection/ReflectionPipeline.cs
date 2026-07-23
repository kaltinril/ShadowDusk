#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.HLSL.Reflection;

/// <summary>
/// The OpenGL/SPIR-V reflection pipeline: reflects the DXIL blob via
/// <see cref="DxilReflectionExtractor"/> and builds the final effect parameter list
/// (merging in FX annotations).
/// </summary>
public sealed class ReflectionPipeline
{
    private readonly DxilReflectionExtractor _extractor;

    /// <summary>Creates the pipeline from a DXIL extractor.</summary>
    /// <param name="extractor">The DXIL reflection extractor.</param>
    public ReflectionPipeline(DxilReflectionExtractor extractor)
    {
        _extractor = extractor;
    }

    /// <summary>
    /// Runs reflection over the given input on the calling thread (the extraction is
    /// synchronous work on every host) and returns the fully assembled
    /// <see cref="ReflectedEffect"/>.
    /// </summary>
    /// <param name="input">The DXIL blob and FX annotations to reflect.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <param name="includeSamplerParameters">
    /// When <see langword="false"/>, a sampler+texture pair folds into the single texture
    /// (Object) parameter instead of emitting a standalone sampler parameter — the D3D
    /// convention (DirectX11's DXBC path and DirectX12 both fold; real mgfxc's DirectX_12
    /// golden carries no separate sampler parameter, confirmed by decoding it directly,
    /// Phase 54 follow-up). Defaults to <see langword="true"/>, preserving OpenGL/Vulkan's
    /// existing (Phase-17/32 proven) behavior.
    /// </param>
    /// <returns>The reflected effect on success, or a <see cref="ShaderError"/> on failure.</returns>
    public Result<ReflectedEffect, ShaderError> Reflect(
        ReflectionInput input,
        CancellationToken ct = default,
        bool includeSamplerParameters = true)
    {
        Result<ReflectedEffect, ShaderError> extractResult = _extractor.Extract(input.DxilBlob, ct);

        if (extractResult.IsFailure)
            return extractResult;

        ReflectedEffect baseEffect  = extractResult.Value;
        IReadOnlyList<ParameterReflection> parameters =
            ParameterListBuilder.Build(baseEffect, input.FxAnnotations, includeSamplerParameters);

        return Result<ReflectedEffect, ShaderError>.Ok(baseEffect with { Parameters = parameters });
    }
}
