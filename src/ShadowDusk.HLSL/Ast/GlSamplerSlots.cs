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
    IReadOnlySet<int> Reserved);
