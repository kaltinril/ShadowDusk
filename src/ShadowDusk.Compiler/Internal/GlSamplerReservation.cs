#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// The OpenGL sampler-register facts of an effect, decided on the preprocessed source with the
/// compile's own macros, like <c>mgfxc</c>: the registers modern
/// <c>SamplerState X : register(sN)</c> declarations reserve (issue #283), and the register an
/// explicit <c>register(sN)</c> on a legacy <c>sampler</c> declaration pins its texture to
/// (issue #299). Shared by the OpenGL target and the raylib converter, which allocate GL sampler
/// slots with the same <c>SpirvCombinedSamplerPairs.ResolveSlots</c>.
/// </summary>
internal static class GlSamplerReservation
{
    /// <summary>
    /// Flattens <paramref name="rawSource"/> (the effect exactly as the author wrote it, before
    /// the pre-parser's SM4 rewrite) with <paramref name="macros"/>, then reads both maps off its
    /// preprocessed view. <paramref name="parsed"/> is the pre-parse of the same source: it
    /// supplies the sampler-to-texture join for the explicit map. The flatten's own warnings are
    /// discarded: the compile's real flatten already reported them.
    /// </summary>
    public static Result<GlSamplerSlots, ShaderError> Collect(
        string rawSource,
        string sourceFileName,
        MacroSet macros,
        IIncludeResolver includeResolver,
        IReadOnlyList<string> additionalIncludePaths,
        FxParseResult parsed)
    {
        Result<PreprocessedSource, ShaderError> flattened = new Preprocessor().Flatten(
            rawSource, sourceFileName, macros, includeResolver, additionalIncludePaths);
        if (flattened.IsFailure)
            return Result<GlSamplerSlots, ShaderError>.Fail(flattened.Error);

        return FxPreParser.CollectGlSamplerSlots(flattened.Value.Text, sourceFileName, parsed);
    }
}
