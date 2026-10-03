#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>
/// The pre-parse of an effect's PREPROCESSED source (issue #308), produced by
/// <see cref="FxPreParser.ParsePreprocessed(string, string)"/>: the recovery for an effect whose
/// legacy sampler syntax the raw pre-parse could not see because an <c>#include</c> or a macro
/// supplies it.
/// </summary>
public sealed record FxPreprocessedParse
{
    /// <summary>
    /// The parse of the preprocessed text. Its <see cref="FxParseResult.StrippedHlsl"/> is the
    /// compiler input (macros expanded, conditionals evaluated, FX blocks stripped, legacy
    /// samplers rewritten); its spans are mapped back onto the author's lines.
    /// </summary>
    public required FxParseResult Parsed { get; init; }

    /// <summary>
    /// The first legacy sampler construct the rewrite could NOT model and so left in
    /// <see cref="FxParseResult.StrippedHlsl"/> (a <c>sampler2D</c> function parameter, for
    /// example), or null when none is left. The compiler will reject it; this is what lets the
    /// caller say why.
    /// </summary>
    public LegacySamplerResidue? Residue { get; init; }

    /// <summary>
    /// The first conditional that tested a macro only the COMPILER defines
    /// (<c>__HLSL_VERSION</c>, <c>__hlsl_dx_compiler</c> …), or null. The managed preprocessor
    /// evaluated it as undefined, so the text may not be the one the compiler's own preprocessor
    /// would have produced, and the caller must not compile it.
    /// </summary>
    public CompilerPredefinedMacroTest? CompilerPredefinedMacro { get; init; }
}

/// <summary>A legacy D3D9 sampler construct left in a source the compiler is given.</summary>
/// <param name="Token">The offending keyword or intrinsic name, as written.</param>
/// <param name="File">The author's file it is in.</param>
/// <param name="Line">Its 1-based line in that file.</param>
/// <param name="Column">Its 1-based column.</param>
public sealed record LegacySamplerResidue(string Token, string File, int Line, int Column);

/// <summary>
/// The answer of <see cref="FxPreParser.HasLegacySamplerResidue(string, string)"/>.
/// </summary>
/// <param name="Found">True when legacy D3D9 sampler syntax is left in the preprocessed text.</param>
/// <param name="CompilerPredefinedMacro">
/// The first conditional that tested a compiler-predefined macro, or null. When set, the
/// preprocessed text (and so <paramref name="Found"/>) was built on the assumption that the
/// macro is undefined, which the compiler does not share.
/// </param>
public sealed record LegacySamplerResidueCheck(bool Found, CompilerPredefinedMacroTest? CompilerPredefinedMacro);

/// <summary>A preprocessor conditional that tested a compiler-predefined macro.</summary>
/// <param name="Name">The macro name.</param>
/// <param name="File">The author's file the conditional is in.</param>
/// <param name="Line">Its 1-based line in that file.</param>
public sealed record CompilerPredefinedMacroTest(string Name, string File, int Line);
