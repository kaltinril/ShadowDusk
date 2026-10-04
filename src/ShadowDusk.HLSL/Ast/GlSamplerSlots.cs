#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>
/// The two facts the OpenGL sampler allocator starts from, both read off the PREPROCESSED source
/// (conditionals evaluated, macros expanded) the way <c>mgfxc</c> reads them. Produced by
/// <see cref="FxPreParser.CollectGlSamplerSlots(string, string, FxParseResult)"/>.
/// </summary>
/// <param name="Explicit">
/// TEXTURE name -> the texture unit an explicit <c>register(sN)</c> on its LEGACY sampler
/// declaration pins it to. The preprocessed counterpart of
/// <see cref="FxParseResult.ExplicitGlSamplerSlots"/> (issue #299).
/// </param>
/// <param name="Reserved">
/// The registers modern <c>SamplerState X : register(sN)</c> declarations take out of circulation.
/// The preprocessed counterpart of <see cref="FxParseResult.ReservedGlSamplerSlots"/>
/// (issue #283).
/// </param>
public sealed record GlSamplerSlots(
    IReadOnlyDictionary<string, int> Explicit,
    IReadOnlySet<int> Reserved)
{
    /// <summary>
    /// SAMPLER name (as the preprocessed view spells it) -> the explicit <c>register(sN)</c> on its
    /// LEGACY declaration, for every legacy sampler a legacy intrinsic (<c>tex2D</c> ...) reads.
    /// Keyed on the sampler rather than the texture so two samplers sharing one texture stay
    /// apart: it is what the overlapping-register check (<c>SD0227</c>, fxc's <c>X4500</c>) reads.
    /// </summary>
    public IReadOnlyDictionary<string, int> LegacySamplerRegisters { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}
