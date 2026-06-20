using System.Text;

namespace ShadowDusk.ShaderToy;

/// <summary>
/// A line-oriented preprocessing pass run before lexing. It:
/// <list type="bullet">
/// <item>collects object-like <c>#define NAME value</c> macros (simple token substitution);</item>
/// <item>strips <c>precision</c> statements and the <c>highp</c>/<c>mediump</c>/<c>lowp</c> qualifiers;</item>
/// <item>rejects function-like macros, and the <c>#if/#ifdef/#ifndef/#else/#endif/#include</c> family,
/// with a located diagnostic (never silently dropped).</item>
/// </list>
/// It preserves line counts exactly (blanked-out lines stay as blank lines) so downstream
/// line/column diagnostics still point at the original source.
/// </summary>
internal sealed class Preprocessor
{
    private readonly Dictionary<string, string> _defines = new(StringComparer.Ordinal);

    /// <summary>Run preprocessing, returning text ready for the lexer (line numbers preserved).</summary>
    public string Process(string source)
    {
        // Normalize line endings so the line counter and the lexer agree.
        string normalized = source.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        var output = new StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNo = i + 1;
            string line = lines[i];
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith('#'))
            {
                ProcessDirective(trimmed, lineNo, line);
                output.Append('\n');
                continue;
            }

            // A `precision …;` statement → blank line (keep line count).
            string noPrecisionStmt = StripPrecisionStatement(trimmed);
            if (noPrecisionStmt.Length == 0 && trimmed.Length != 0)
            {
                output.Append('\n');
                continue;
            }

            output.Append(line);
            if (i < lines.Length - 1)
            {
                output.Append('\n');
            }
        }

        string body = output.ToString();
        body = StripPrecisionQualifiers(body);
        body = ApplyDefines(body);
        return body;
    }

    private void ProcessDirective(string directive, int lineNo, string original)
    {
        int col = original.IndexOf('#') + 1;
        string content = directive[1..].TrimStart();

        if (content.StartsWith("define", StringComparison.Ordinal) &&
            (content.Length == 6 || char.IsWhiteSpace(content[6])))
        {
            ProcessDefine(content[6..].Trim(), lineNo, col);
            return;
        }

        if (content.StartsWith("undef", StringComparison.Ordinal))
        {
            string name = content[5..].Trim();
            _defines.Remove(name);
            return;
        }

        if (content.StartsWith("version", StringComparison.Ordinal) ||
            content.StartsWith("extension", StringComparison.Ordinal) ||
            content.StartsWith("pragma", StringComparison.Ordinal) ||
            content.Length == 0)
        {
            // Harmless directives → ignore.
            return;
        }

        // Everything else in the preprocessor family is out of scope.
        string keyword = content.Split(new[] { ' ', '\t' }, 2)[0];
        throw new ConvertException(
            $"Unsupported preprocessor directive '#{keyword}'. Conditional compilation and includes " +
            "are outside the supported subset.",
            lineNo, col, "#" + keyword);
    }

    private void ProcessDefine(string rest, int lineNo, int col)
    {
        if (rest.Length == 0)
        {
            throw new ConvertException("Malformed '#define'.", lineNo, col, "#define");
        }

        // Function-like macro: NAME immediately followed by '(' with no space.
        int nameEnd = 0;
        while (nameEnd < rest.Length && (char.IsLetterOrDigit(rest[nameEnd]) || rest[nameEnd] == '_'))
        {
            nameEnd++;
        }

        string name = rest[..nameEnd];
        if (name.Length == 0)
        {
            throw new ConvertException("Malformed '#define' (missing macro name).", lineNo, col, "#define");
        }

        if (nameEnd < rest.Length && rest[nameEnd] == '(')
        {
            throw new ConvertException(
                $"Function-like macro '#define {name}(...)' is outside the supported subset. " +
                "Only object-like '#define NAME value' constants are supported.",
                lineNo, col, "#define " + name + "(");
        }

        string value = rest[nameEnd..].Trim();
        _defines[name] = value;
    }

    /// <summary>If the (trimmed) line is a bare <c>precision …;</c> statement, return empty.</summary>
    private static string StripPrecisionStatement(string trimmed)
    {
        if (trimmed.StartsWith("precision", StringComparison.Ordinal) &&
            (trimmed.Length == 9 || char.IsWhiteSpace(trimmed[9])))
        {
            return string.Empty;
        }

        return trimmed;
    }

    /// <summary>Remove standalone <c>highp</c>/<c>mediump</c>/<c>lowp</c> tokens from the body.</summary>
    private static string StripPrecisionQualifiers(string body)
    {
        foreach (string q in new[] { "highp", "mediump", "lowp" })
        {
            body = ReplaceWholeWord(body, q, string.Empty);
        }

        return body;
    }

    private string ApplyDefines(string body)
    {
        // Multiple passes so a macro can expand to another macro (bounded to avoid cycles).
        for (int pass = 0; pass < 8 && _defines.Count > 0; pass++)
        {
            bool changed = false;
            foreach (KeyValuePair<string, string> kv in _defines)
            {
                string next = ReplaceWholeWord(body, kv.Key, kv.Value);
                if (!ReferenceEquals(next, body) && next != body)
                {
                    body = next;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        return body;
    }

    /// <summary>
    /// Replace every whole-word occurrence of <paramref name="word"/> with <paramref name="with"/>,
    /// skipping matches inside string/identifier boundaries (only token-boundary replacements).
    /// </summary>
    private static string ReplaceWholeWord(string text, string word, string with)
    {
        if (word.Length == 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        int i = 0;
        bool replaced = false;
        while (i < text.Length)
        {
            if (text[i] == word[0] &&
                i + word.Length <= text.Length &&
                string.CompareOrdinal(text, i, word, 0, word.Length) == 0)
            {
                bool leftOk = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
                int after = i + word.Length;
                bool rightOk = after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_');
                if (leftOk && rightOk)
                {
                    sb.Append(with);
                    i = after;
                    replaced = true;
                    continue;
                }
            }

            sb.Append(text[i]);
            i++;
        }

        return replaced ? sb.ToString() : text;
    }
}
