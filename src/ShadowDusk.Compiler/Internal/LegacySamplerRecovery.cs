#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// The legacy-sampler recovery (issue #308). The FX pre-parser rewrites D3D9 sampler syntax
/// (<c>sampler2D</c>, <c>sampler_state</c>, <c>tex2D</c>) into the SM4 form DXC compiles, but it
/// reads the RAW tokens of the MAIN file: a legacy sampler declared in an <c>#include</c>d file,
/// one whose register clause or whole declaration comes out of a macro
/// (<c>DECLARE_TEXTURE(S, 1)</c>, the MonoGame <c>Macros.fxh</c> idiom), or a <c>tex2D</c> inside
/// a macro body (<c>SAMPLE_TEXTURE</c>) reaches DXC unrewritten and DXC rejects it.
/// <c>mgfxc</c> compiles all of them, because it preprocesses first and parses second.
///
/// <para><b>How it stays out of the way.</b> It is only consulted AFTER a shader compile has
/// failed, and only when the text that compile was given still holds legacy sampler syntax once
/// preprocessed. An effect that compiles from its raw source is never touched, so nothing it
/// emits can move. When it does apply, the pre-parse is repeated on the preprocessed source
/// (<see cref="FxPreParser.ParsePreprocessed"/>, the managed preprocessor that already decides
/// the OpenGL sampler registers) and the compile runs once more on that text.</para>
///
/// <para><b>Fail loudly.</b> A shape the recovery cannot model is <c>SD0016</c>, appended to the
/// compiler's own diagnostics: legacy sampler syntax the rewrite still could not convert, a
/// conditional on a compiler-predefined macro (the managed preprocessor cannot know its value, so
/// its text may not be the one DXC would compile), or a preprocessed view that cannot be
/// built.</para>
/// </summary>
internal static class LegacySamplerRecovery
{
    /// <summary>The registered diagnostic code for "legacy sampler syntax could not be rewritten".</summary>
    public const string ErrorCode = "SD0016";

    /// <summary>What <see cref="Evaluate"/> decided.</summary>
    internal abstract record Outcome
    {
        /// <summary>The failure has nothing to do with legacy sampler syntax: keep its diagnostics.</summary>
        internal sealed record NotApplicable : Outcome;

        /// <summary>Compile again from the preprocessed pre-parse.</summary>
        /// <param name="Parsed">The pre-parse of the preprocessed source.</param>
        /// <param name="FlattenedRawSource">The raw source, <c>#include</c>s inlined: what the
        /// OpenGL sampler-register reading is taken from.</param>
        /// <param name="Warnings">The flattener's non-fatal diagnostics.</param>
        internal sealed record Retry(
            FxPreprocessedParse Parsed,
            string FlattenedRawSource,
            IReadOnlyList<ShaderError> Warnings) : Outcome;

        /// <summary>The preprocessed source itself is in error (an <c>FXnnnn</c> diagnostic).</summary>
        internal sealed record Rejected(ShaderError Error) : Outcome;

        /// <summary>The recovery cannot be applied faithfully: append this <c>SD0016</c>.</summary>
        internal sealed record Unmodelled(ShaderError Error) : Outcome;
    }

    /// <summary>
    /// Decides, for an effect whose shader compile just failed, whether repeating the pre-parse on
    /// the preprocessed source can help.
    /// </summary>
    public static Outcome Evaluate(
        string rawSource,
        string sourceFileName,
        MacroSet macros,
        IIncludeResolver includeResolver,
        IReadOnlyList<string> additionalIncludePaths)
    {
        // Re-create what the failed compile was given: the raw pre-parse's rewritten text,
        // flattened. Both steps already succeeded once, so a failure here is not this code's to
        // report.
        Result<FxParseResult, FxParseError> rawParse = FxPreParser.Parse(rawSource, sourceFileName);
        if (rawParse.IsFailure)
            return new Outcome.NotApplicable();

        var preprocessor = new Preprocessor();
        Result<PreprocessedSource, ShaderError> compiled = preprocessor.Flatten(
            rawParse.Value.StrippedHlsl, sourceFileName, macros, includeResolver, additionalIncludePaths);
        if (compiled.IsFailure)
            return new Outcome.NotApplicable();

        Result<LegacySamplerResidueCheck, ShaderError> residue =
            FxPreParser.HasLegacySamplerResidue(compiled.Value.Text, sourceFileName);
        if (residue.IsFailure)
        {
            // The view cannot be built, so whether the rewrite missed something is unknowable.
            // Say so only when the effect uses legacy sampler syntax at all.
            return FxPreParser.MentionsLegacySamplerSyntax(compiled.Value.Text)
                ? new Outcome.Unmodelled(Unmodelled(
                    residue.Error.File, residue.Error.Line,
                    "the effect uses legacy D3D9 sampler syntax (sampler2D / sampler_state / tex2D) and " +
                    "ShadowDusk could not preprocess it to check whether an #include or a macro hides a " +
                    $"declaration from the SM4 rewrite: {residue.Error.Message}"))
                : new Outcome.NotApplicable();
        }
        if (!residue.Value.Found)
        {
            // Nothing legacy is left in what the compiler saw, as far as the managed view can tell.
            // It cannot tell when a conditional hangs on a macro only the compiler defines: the
            // branch it skipped may be the one DXC compiled. Say so if that branch could hold the
            // syntax at all.
            return residue.Value.CompilerPredefinedMacro is { } predefined &&
                   FxPreParser.MentionsLegacySamplerSyntax(compiled.Value.Text)
                ? new Outcome.Unmodelled(PredefinedMacroError(predefined))
                : new Outcome.NotApplicable();
        }

        Result<PreprocessedSource, ShaderError> flattenedRaw = preprocessor.Flatten(
            rawSource, sourceFileName, macros, includeResolver, additionalIncludePaths);
        if (flattenedRaw.IsFailure)
            return new Outcome.NotApplicable();

        Result<FxPreprocessedParse, ShaderError> parsed =
            FxPreParser.ParsePreprocessed(flattenedRaw.Value.Text, sourceFileName);
        if (parsed.IsFailure)
        {
            // An FXnnnn diagnostic is the pre-parser's verdict on the preprocessed source, which
            // is the text mgfxc parses too: it is the effect's real error (a tex2Dlod inside a
            // macro is FX0012, with its location). Anything else is the view failing to build.
            return parsed.Error.Code.StartsWith("FX", StringComparison.Ordinal)
                ? new Outcome.Rejected(parsed.Error)
                : new Outcome.Unmodelled(Unmodelled(
                    parsed.Error.File, parsed.Error.Line,
                    "legacy D3D9 sampler syntax reaches the compiler through an #include or a macro, and " +
                    $"ShadowDusk could not preprocess the effect to rewrite it: {parsed.Error.Message}"));
        }

        if (parsed.Value.CompilerPredefinedMacro is { } predefinedInRaw)
            return new Outcome.Unmodelled(PredefinedMacroError(predefinedInRaw));

        return new Outcome.Retry(parsed.Value, flattenedRaw.Value.Text, flattenedRaw.Value.Warnings);
    }

    /// <summary>
    /// Turns <see cref="Evaluate"/>'s verdict into the final result of a compile or conversion
    /// whose first attempt (<paramref name="first"/>) failed on a DXC shader compile. Shared by
    /// <c>CompilationPipeline.Run</c> and the raylib and SkSL converters (issue #327), so the
    /// three agree on what each outcome means:
    /// <list type="bullet">
    /// <item><see cref="Outcome.Retry"/>: <paramref name="retry"/> runs the same work again from
    /// the preprocessed pre-parse. Its result stands, unless it fails AND legacy syntax is still
    /// in the compiler's input: then <c>SD0016</c> names the unmodelled shape after the compiler's
    /// own verbatim diagnostics.</item>
    /// <item><see cref="Outcome.Rejected"/>: the pre-parser's own <c>FXnnnn</c> verdict on the
    /// preprocessed source replaces the compiler's diagnostics (it is the effect's real error, at
    /// the author's line).</item>
    /// <item><see cref="Outcome.Unmodelled"/>: the first attempt's diagnostics, then the <c>SD0016</c>.</item>
    /// <item><see cref="Outcome.NotApplicable"/>: the first attempt's result, untouched.</item>
    /// </list>
    /// </summary>
    public static Result<T, ShaderError[]> Apply<T>(
        Result<T, ShaderError[]> first,
        Outcome outcome,
        Func<Outcome.Retry, Result<T, ShaderError[]>> retry)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(retry);

        switch (outcome)
        {
            case Outcome.Retry recovered:
            {
                Result<T, ShaderError[]> second = retry(recovered);
                if (second.IsSuccess || recovered.Parsed.Residue is null)
                    return second;

                return Result<T, ShaderError[]>.Fail(
                    [.. second.Error, ResidueError(recovered.Parsed.Residue)]);
            }
            case Outcome.Rejected rejected:
                return Result<T, ShaderError[]>.Fail([rejected.Error]);
            case Outcome.Unmodelled unmodelled:
                return Result<T, ShaderError[]>.Fail([.. first.Error, unmodelled.Error]);
            default:
                return first;
        }
    }

    private static ShaderError PredefinedMacroError(CompilerPredefinedMacroTest predefined) => Unmodelled(
        predefined.File, predefined.Line,
        "the effect uses legacy D3D9 sampler syntax (sampler2D / sampler_state / tex2D) that may reach " +
        "the compiler through an #include or a macro, where the SM4 rewrite has to run on the " +
        $"preprocessed source, but a conditional here tests the compiler-predefined macro '{predefined.Name}', " +
        "whose value ShadowDusk's managed preprocessor cannot know. It will not compile a preprocessed text " +
        "that may differ from the one the compiler would produce. Declare the sampler and its tex2D call " +
        "directly in the .fx file, or drop the test on the predefined macro.");

    /// <summary>
    /// The <c>SD0016</c> for a recovery that ran and still failed with legacy sampler syntax left
    /// in the compiler's input: a shape the SM4 rewrite does not model.
    /// </summary>
    public static ShaderError ResidueError(LegacySamplerResidue residue) => new(
        File: residue.File,
        Line: residue.Line,
        Column: residue.Column,
        Code: ErrorCode,
        Message: $"legacy D3D9 sampler syntax ('{residue.Token}') is still in the source after ShadowDusk's SM4 " +
                 "rewrite ran on the preprocessed effect, so the compiler was given a construct it does not " +
                 "have. The rewrite models a global 'sampler' / 'sampler2D' declaration (bare, with a " +
                 "register, or with a sampler_state block) read by tex2D or tex2Dgrad; it does not model " +
                 "this shape (for example a sampler2D function parameter). Use Texture2D + SamplerState + " +
                 ".Sample for it. The FNA (fx_2_0) and DirectX_11 targets compile legacy sampler syntax " +
                 "natively.");

    private static ShaderError Unmodelled(string file, int line, string message) => new(
        File: file,
        Line: line,
        Column: 0,
        Code: ErrorCode,
        Message: message);
}
