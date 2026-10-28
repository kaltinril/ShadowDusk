#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// Which techniques an effect compiles (issue #407). <c>mgfxc</c> preprocesses first and finds
/// techniques second, so a technique inside an inactive <c>#if</c> does not exist, one in an
/// <c>#include</c>d file does, and <c>compile VS_SHADERMODEL V()</c> names the profile the macro
/// expands to. The pre-parse reads the raw main file, which gets all three wrong, so the list is
/// read off the preprocessed translation unit instead
/// (<see cref="FxPreParser.ParseTechniquesPreprocessed"/>). Shared by every target of
/// <c>CompilationPipeline</c> (the FNA one included) and by the raylib and SkSL converters, so
/// they all agree on what an effect's techniques are.
/// </summary>
internal static class EffectTechniques
{
    /// <summary>What <see cref="Resolve"/> decided.</summary>
    /// <param name="Techniques">The techniques to compile, in source order.</param>
    /// <param name="ViewWarning">
    /// An <c>SD0009</c> warning for a preprocessed view that could not be built (a directive or
    /// <c>#if</c> expression the managed preprocessor does not model). The compile then runs on
    /// the raw technique list, as it did before issue #407, and the caller adds this to its
    /// warnings. Never an error here: the compiler accepts such a source.
    /// </param>
    internal sealed record Resolution(IReadOnlyList<TechniqueInfo> Techniques, ShaderError? ViewWarning);

    /// <summary>
    /// Reads the techniques of <paramref name="rawSource"/> (the effect exactly as the author
    /// wrote it) off its preprocessed translation unit, flattened with <paramref name="macros"/>,
    /// and decides which ones the compile uses.
    /// </summary>
    /// <param name="rawSource">The effect source before the pre-parse.</param>
    /// <param name="sourceFileName">Display name used in diagnostics.</param>
    /// <param name="macros">The compile's platform and user macros.</param>
    /// <param name="includeResolver">The compile's include resolver.</param>
    /// <param name="additionalIncludePaths">The compile's extra include directories.</param>
    /// <param name="rawParsed">The pre-parse of the raw source.</param>
    /// <returns>The decision, or the pre-parser's <c>FXnnnn</c> verdict on a technique block of
    /// the preprocessed text (the effect's real error, at the author's line).</returns>
    public static Result<Resolution, ShaderError> Resolve(
        string rawSource,
        string sourceFileName,
        MacroSet macros,
        IIncludeResolver includeResolver,
        IReadOnlyList<string> additionalIncludePaths,
        FxParseResult rawParsed)
    {
        ArgumentNullException.ThrowIfNull(rawParsed);

        Result<PreprocessedSource, ShaderError> flattened = new Preprocessor().Flatten(
            rawSource, sourceFileName, macros, includeResolver, additionalIncludePaths);
        if (flattened.IsFailure)
            return Result<Resolution, ShaderError>.Fail(flattened.Error);

        Result<FxPreprocessedTechniques, ShaderError> view =
            FxPreParser.ParseTechniquesPreprocessed(flattened.Value.Text, sourceFileName);
        if (view.IsFailure)
        {
            return string.Equals(view.Error.Code, FxMacroPreprocessorErrorCode, StringComparison.Ordinal)
                ? Result<Resolution, ShaderError>.Ok(new Resolution(rawParsed.Techniques, view.Error with
                {
                    Severity = ShaderErrorSeverity.Warning,
                    Message = view.Error.Message + " The techniques were read from the raw source instead, " +
                              "so a technique inside an inactive #if, or one an #include brings in, is not accounted for.",
                }))
                : Result<Resolution, ShaderError>.Fail(view.Error);
        }

        FxPreprocessedTechniques techniques = view.Value;
        if (!techniques.HasMacroProducedTechniques)
            return Result<Resolution, ShaderError>.Ok(new Resolution(techniques.Techniques, null));

        // A technique a macro produces (MonoGame's TECHNIQUE(name, vs, ps)) stays where it was
        // before issue #407: when the main file has no technique written out, the caller's
        // zero-technique fallback (Phase 41) owns the effect, and otherwise only the techniques
        // the author wrote are compiled. That fallback is gated per target for a reason this
        // change does not revisit (the OpenGL macro set selects MonoGame's legacy branch).
        return Result<Resolution, ShaderError>.Ok(new Resolution(
            rawParsed.Techniques.Count == 0 ? [] : techniques.LiteralTechniques, null));
    }

    /// <summary>The managed preprocessor's registered "view could not be built" code.</summary>
    private const string FxMacroPreprocessorErrorCode = "SD0009";
}
