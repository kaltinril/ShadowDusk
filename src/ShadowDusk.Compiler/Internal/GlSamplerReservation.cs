#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// The OpenGL sampler registers that modern <c>SamplerState X : register(sN)</c> declarations
/// reserve, decided on the preprocessed source with the compile's own macros (issue #283).
/// Shared by the OpenGL target and the raylib converter, which allocate GL sampler slots with
/// the same <c>SpirvCombinedSamplerPairs.ResolveSlots</c>.
/// </summary>
internal static class GlSamplerReservation
{
    /// <summary>
    /// Flattens <paramref name="rawSource"/> (the effect exactly as the author wrote it, before
    /// the pre-parser's SM4 rewrite) with <paramref name="macros"/>, then reads the reservations
    /// off its preprocessed view. The flatten's own warnings are discarded: the compile's real
    /// flatten already reported them.
    /// </summary>
    public static Result<IReadOnlySet<int>, ShaderError> Collect(
        string rawSource,
        string sourceFileName,
        MacroSet macros,
        IIncludeResolver includeResolver,
        IReadOnlyList<string> additionalIncludePaths)
    {
        Result<PreprocessedSource, ShaderError> flattened = new Preprocessor().Flatten(
            rawSource, sourceFileName, macros, includeResolver, additionalIncludePaths);
        if (flattened.IsFailure)
            return Result<IReadOnlySet<int>, ShaderError>.Fail(flattened.Error);

        return FxPreParser.CollectReservedGlSamplerSlots(flattened.Value.Text, sourceFileName);
    }
}
