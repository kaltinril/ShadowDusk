#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>
/// The techniques of an effect's PREPROCESSED translation unit (issue #407), produced by
/// <see cref="FxPreParser.ParseTechniquesPreprocessed(string, string)"/>: conditionals evaluated
/// with the compile's macros, <c>#include</c>s inlined and macros expanded, which is the text
/// <c>mgfxc</c> reads its techniques from. Spans are mapped back onto the author's lines.
/// </summary>
public sealed record FxPreprocessedTechniques
{
    /// <summary>Every technique of the translation unit, in source order.</summary>
    public required IReadOnlyList<TechniqueInfo> Techniques { get; init; }

    /// <summary>
    /// The subset of <see cref="Techniques"/> whose <c>technique</c> keyword the author wrote
    /// (in the main file or an <c>#include</c>d one), in source order. A technique missing from
    /// this list was produced by a macro expansion, such as MonoGame's
    /// <c>TECHNIQUE(name, vs, ps)</c>.
    /// </summary>
    public required IReadOnlyList<TechniqueInfo> LiteralTechniques { get; init; }

    /// <summary>True when at least one technique was produced by a macro expansion.</summary>
    public bool HasMacroProducedTechniques => LiteralTechniques.Count != Techniques.Count;
}
