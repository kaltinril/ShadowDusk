namespace ShadowDusk.ShaderToy;

/// <summary>
/// Lightweight, public classification of a source string as ShaderToy/GLSL vs an HLSL <c>.fx</c>
/// effect, for hosts that accept either and must decide whether to route through
/// <see cref="ShaderToyConverter"/> first (the CLI, the WASM fiddle, any consumer wiring ShaderToy
/// input). This is a cheap STRUCTURAL sniff on comment/string-stripped text, NOT a parse — the real
/// validation is the converter's job, which fails loudly if the sniff guessed wrong.
/// </summary>
public static class ShaderToySource
{
    /// <summary>
    /// True when <paramref name="source"/> looks like a ShaderToy / GLSL image shader: it contains a
    /// <c>mainImage</c> or a fragment-style <c>void main</c> entry and NO top-level HLSL
    /// <c>technique</c> block. An HLSL effect (which always has a <c>technique</c>) returns false, so
    /// the caller can treat it as <c>.fx</c>. Comments and string literals are ignored.
    /// </summary>
    public static bool LooksLikeShaderToyGlsl(string source)
    {
        if (string.IsNullOrEmpty(source))
            return false;

        string stripped = StripCommentsAndStrings(source);

        // An HLSL effect is unambiguously a .fx — never route a 'technique' source to the converter.
        if (ContainsWord(stripped, "technique"))
            return false;

        return ContainsWord(stripped, "mainImage") || ContainsVoidMain(stripped);
    }

    // Whole-word, case-sensitive (GLSL/HLSL identifiers are case-sensitive) match of an identifier.
    private static bool ContainsWord(string text, string word)
    {
        int from = 0;
        while (true)
        {
            int idx = text.IndexOf(word, from, System.StringComparison.Ordinal);
            if (idx < 0)
                return false;

            bool leftOk = idx == 0 || !IsIdentChar(text[idx - 1]);
            int end = idx + word.Length;
            bool rightOk = end >= text.Length || !IsIdentChar(text[end]);
            if (leftOk && rightOk)
                return true;

            from = idx + 1;
        }
    }

    // Matches a fragment-style entry: the keyword 'void', whitespace, then the identifier 'main'.
    private static bool ContainsVoidMain(string text)
    {
        int from = 0;
        while (true)
        {
            int idx = text.IndexOf("void", from, System.StringComparison.Ordinal);
            if (idx < 0)
                return false;

            bool leftOk = idx == 0 || !IsIdentChar(text[idx - 1]);
            int p = idx + 4;
            if (leftOk && p < text.Length && char.IsWhiteSpace(text[p]))
            {
                while (p < text.Length && char.IsWhiteSpace(text[p]))
                    p++;
                if (p < text.Length && text.AsSpan(p).StartsWith("main"))
                {
                    int end = p + 4;
                    bool rightOk = end >= text.Length || !IsIdentChar(text[end]);
                    if (rightOk)
                        return true;
                }
            }

            from = idx + 1;
        }
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Replaces // line comments, /* */ block comments, and double-quoted string literals with spaces
    // so a 'mainImage'/'technique' token inside a comment or string never trips the sniff.
    private static string StripCommentsAndStrings(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                if (i < text.Length) { sb.Append(' '); i++; }     // '*'
                if (i < text.Length) { sb.Append(' '); i++; }     // '/'
                continue;
            }

            if (c == '"')
            {
                sb.Append(' '); i++;
                while (i < text.Length && text[i] != '"' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(' '); i++; }
                    sb.Append(' '); i++;
                }
                if (i < text.Length && text[i] == '"') { sb.Append(' '); i++; }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
