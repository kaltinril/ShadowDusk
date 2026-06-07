#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.HLSL.Reflection;

/// <summary>
/// The OpenGL/SPIR-V reflection pipeline: reflects the DXIL blob via
/// <see cref="DxilReflectionExtractor"/>, cross-checks texture/sampler binding slots against
/// the SPIR-V blob, and builds the final effect parameter list (merging in FX annotations).
/// </summary>
public sealed class ReflectionPipeline
{
    private readonly DxilReflectionExtractor _extractor;
    private readonly SpvReflectionVerifier   _verifier;

    /// <summary>Creates the pipeline from a DXIL extractor and a SPIR-V binding verifier.</summary>
    /// <param name="extractor">The DXIL reflection extractor.</param>
    /// <param name="verifier">The SPIR-V binding-slot verifier.</param>
    public ReflectionPipeline(DxilReflectionExtractor extractor, SpvReflectionVerifier verifier)
    {
        _extractor = extractor;
        _verifier  = verifier;
    }

    /// <summary>
    /// Runs reflection over the given input and returns the fully assembled
    /// <see cref="ReflectedEffect"/>.
    /// </summary>
    /// <param name="input">The DXIL + SPIR-V blobs and FX annotations to reflect.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The reflected effect on success, or a <see cref="ShaderError"/> on failure.</returns>
    public async Task<Result<ReflectedEffect, ShaderError>> ReflectAsync(
        ReflectionInput input,
        CancellationToken ct = default)
    {
        Result<ReflectedEffect, ShaderError> extractResult =
            await Task.Run(() => _extractor.Extract(input.DxilBlob, ct), ct).ConfigureAwait(false);

        if (extractResult.IsFailure)
            return extractResult;

        Result<BindingSlotMap, ShaderError> bindingsResult =
            _verifier.GetBindings(input.SpirVBlob, ct);

        if (bindingsResult.IsFailure)
            return Result<ReflectedEffect, ShaderError>.Fail(bindingsResult.Error);

        ReflectedEffect baseEffect  = extractResult.Value;
        IReadOnlyList<ParameterReflection> parameters =
            ParameterListBuilder.Build(baseEffect, input.FxAnnotations);

        return Result<ReflectedEffect, ShaderError>.Ok(baseEffect with { Parameters = parameters });
    }
}
