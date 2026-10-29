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

    /// <summary>
    /// TEXTURE names (as the preprocessed view spells them) that a LEGACY sampler samples through
    /// (its synthesized texture, or the one its <c>sampler_state</c> block names). fxc allocates the
    /// OpenGL units of these AFTER every modern (texture, sampler) pair, so the allocator needs to
    /// tell the two kinds apart.
    /// </summary>
    public IReadOnlySet<string> LegacyTextures { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// LEGACY sampler name -> the texture it samples through, both as the preprocessed view spells
    /// them. The DirectX 11 sampler table needs the join: <c>mgfxc</c>'s record for a legacy
    /// sampler's texture carries that SAMPLER's DXBC binding, not the texture's.
    /// </summary>
    public IReadOnlyDictionary<string, string> LegacySamplerTextures { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Every declaration (as the preprocessed view spells it) of a resource that lives in a
    /// <c>t</c> register and carries an explicit <c>register(tN)</c>, read or not: any
    /// <c>texture*</c> type (legacy <c>texture</c>/<c>texture2D</c> and modern <c>Texture2D</c>
    /// alike), <c>Buffer</c>, <c>StructuredBuffer</c>, <c>ByteAddressBuffer</c>, <c>tbuffer</c>; a
    /// stage-qualified <c>register(ps, tN)</c> applies to that stage only. fxc takes every one of
    /// these registers out of circulation, and keeps a directly read resource on its register; the
    /// DirectX 11 texture slot assignment allocates legacy samplers' textures around them.
    /// </summary>
    internal IReadOnlyList<ExplicitTextureRegister> ExplicitTextureRegisters { get; init; } = [];
}

/// <summary>An explicit <c>t</c> register on a declaration: name, register, and the stage
/// (<c>"vs"</c>/<c>"ps"</c>) when the clause is stage-qualified, else <c>null</c>.</summary>
internal sealed record ExplicitTextureRegister(string Name, int Slot, string? Stage);
