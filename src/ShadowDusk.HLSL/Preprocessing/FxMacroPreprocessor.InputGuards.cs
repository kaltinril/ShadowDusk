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
    // Set only by ProcessForInputGuards: the hook in the expansion loop records, and changes nothing.
    private bool _guardMode;
    private BlockedSelfReference? _blockedSelfReference;

    /// <summary>
    /// A macro met again while its own expansion is still being read, where it would be expanded
    /// if C did not forbid it: an object-like macro, or a function-like one whose <c>(</c> comes
    /// out of that same expansion (<c>#define F(x) F(x)</c>, <c>#define X X</c>, two macros that
    /// name each other, in code or in an <c>#if</c>). C leaves the name as it is; vkd3d-shader's
    /// preprocessor never returns (issue #449). A function-like name whose <c>(</c> comes from the
    /// text after the expansion (<c>ID(ID)(1)</c>) is not one: vkd3d expands that once more and
    /// finishes, as measured, and so does a function-like name with no <c>(</c> after it.
    /// </summary>
    /// <param name="Name">The macro.</param>
    /// <param name="PhysicalLine">The 1-based physical line of the flattened source the name sits on, or 0 inside a directive.</param>
    /// <param name="File">The author's file of the text being expanded.</param>
    /// <param name="Line">The author's line of the text being expanded (the start of the run of text, or the directive).</param>
    internal sealed record BlockedSelfReference(string Name, int PhysicalLine, string File, int Line);

    /// <summary>What <see cref="ProcessForInputGuards"/> found.</summary>
    /// <param name="View">The compiler-input view (line N holds what line N of the source held).</param>
    /// <param name="SelfReference">The first blocked self re-expansion, or null.</param>
    internal sealed record InputGuardView(string View, BlockedSelfReference? SelfReference);

    /// <summary>
    /// The compiler-input view of <paramref name="flattenedSource"/> (see
    /// <see cref="ProcessForCompiler"/>), for the native-input guards, and the first blocked self
    /// re-expansion met while building it.
    /// </summary>
    internal static Result<InputGuardView, ShaderError> ProcessForInputGuards(string flattenedSource, string sourceFile)
    {
        var preprocessor = new FxMacroPreprocessor(sourceFile, compilerInput: true) { _guardMode = true };
        Result<string, ShaderError> view = preprocessor.Run(flattenedSource);
        return view.IsFailure
            ? Result<InputGuardView, ShaderError>.Fail(view.Error)
            : Result<InputGuardView, ShaderError>.Ok(new InputGuardView(view.Value, preprocessor._blockedSelfReference));
    }

    /// <summary>Records <paramref name="name"/> when it is a blocked self re-expansion (guard mode only).</summary>
    private void NoteBlockedName(Tok name, List<Tok> stack, string file, int line)
    {
        if (_blockedSelfReference is not null ||
            name.Kind != TokKind.Identifier ||
            !name.Hide.Contains(name.Text) ||
            !_macros.TryGetValue(name.Text, out Macro? macro))
        {
            return;
        }

        if (macro.Parameters is not null)
        {
            // Function-like: only when the '(' that would make it a call is part of the same
            // expansion (it carries the macro in its own hide set).
            int look = stack.Count - 1;
            while (look >= 0 && stack[look].IsWhitespace)
                look--;
            if (look < 0 || stack[look].Kind != TokKind.Punct || stack[look].Text != "(" ||
                !stack[look].Hide.Contains(name.Text))
            {
                return;
            }
        }

        _blockedSelfReference = new BlockedSelfReference(name.Text, name.Line, file, line);
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
