#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Preprocessing;

/// <summary>
/// Guard mode: the preprocessed view plus what the checks that keep a native compiler away from
/// input it cannot survive need to know about it. Nothing here changes the view or what any other
/// mode does.
/// </summary>
internal sealed partial class FxMacroPreprocessor
{
    /// <summary>What <see cref="ProcessForInputGuards"/> found.</summary>
    /// <param name="View">The compiler-input view (line N holds what line N of the source held).</param>
    internal sealed record InputGuardView(string View);

    /// <summary>
    /// The compiler-input view of <paramref name="flattenedSource"/> (see
    /// <see cref="ProcessForCompiler"/>), for the native-input guards.
    /// </summary>
    internal static Result<InputGuardView, ShaderError> ProcessForInputGuards(string flattenedSource, string sourceFile)
    {
        var preprocessor = new FxMacroPreprocessor(sourceFile, compilerInput: true);
        Result<string, ShaderError> view = preprocessor.Run(flattenedSource);
        return view.IsFailure
            ? Result<InputGuardView, ShaderError>.Fail(view.Error)
            : Result<InputGuardView, ShaderError>.Ok(new InputGuardView(view.Value));
    }

    /// <summary>
    /// The 1-based (line, column) of every cast to <c>void</c> in <paramref name="text"/>:
    /// <c>(</c> <c>void</c> <c>)</c> followed by the start of an expression (issue #448). A
    /// parameter list (<c>F(void)</c>) is followed by <c>{</c>, <c>;</c>, <c>:</c> or <c>)</c>, never
    /// by an operand. Comments are skipped, and so are directive lines when
    /// <paramref name="skipDirectives"/> is set (the view keeps <c>#pragma</c>/<c>#line</c> lines;
    /// a raw source's <c>#define</c> bodies are not code until expanded).
    /// </summary>
    internal static IReadOnlyList<(int Line, int Column)> FindVoidCasts(string text, bool skipDirectives)
    {
        string stripped = StripComments(text.Replace("\r\n", "\n").Replace('\r', '\n'), keepWidth: true, []);
        string[] lines = stripped.Split('\n');
        var tokens = new List<(Tok Tok, int Line, int Column)>();
        var lineTokens = new List<Tok>();
        bool continuedDirective = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool directive = continuedDirective || line.TrimStart(' ', '\t', '\f', '\v').StartsWith('#');
            continuedDirective = directive && line.EndsWith('\\');
            if (directive && skipDirectives)
                continue;

            lineTokens.Clear();
            Tokenize(line, lineTokens, keepWhitespace: true);
            int column = 1;
            foreach (Tok t in lineTokens)
            {
                if (!t.IsWhitespace)
                    tokens.Add((t, i + 1, column));
                column += t.Text.Length;
            }
        }

        var casts = new List<(int, int)>();
        for (int i = 0; i + 3 < tokens.Count; i++)
        {
            if (IsPunct(tokens[i].Tok, "(") &&
                tokens[i + 1].Tok is { Kind: TokKind.Identifier, Text: "void" } &&
                IsPunct(tokens[i + 2].Tok, ")") &&
                StartsOperand(tokens[i + 3].Tok))
            {
                casts.Add((tokens[i].Line, tokens[i].Column));
            }
        }
        return casts;

        static bool IsPunct(Tok t, string text) => t.Kind == TokKind.Punct && t.Text == text;

        static bool StartsOperand(Tok t) =>
            t.Kind is TokKind.Identifier or TokKind.Number or TokKind.String or TokKind.Char ||
            (t.Kind == TokKind.Punct && t.Text is "(" or "-" or "+" or "!" or "~" or "++" or "--");
    }
}
