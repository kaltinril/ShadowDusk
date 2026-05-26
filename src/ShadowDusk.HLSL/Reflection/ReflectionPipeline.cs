#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.HLSL.Reflection;

public sealed class ReflectionPipeline
{
    private readonly DxilReflectionExtractor _extractor;
    private readonly SpvReflectionVerifier   _verifier;

    public ReflectionPipeline(DxilReflectionExtractor extractor, SpvReflectionVerifier verifier)
    {
        _extractor = extractor;
        _verifier  = verifier;
    }

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
