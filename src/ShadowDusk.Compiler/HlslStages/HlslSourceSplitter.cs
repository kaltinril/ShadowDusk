#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;

namespace ShadowDusk.Compiler.HlslStages;

/// <summary>
/// Splits one HLSL source into its top-level declarations, working on a copy with comments and
/// string contents blanked (<see cref="SlangSourceMask"/>) so braces, semicolons and keywords
/// inside comments or strings are never mistaken for code. The masked copy has the same length
/// and newlines as the source, so every position maps straight back onto the original text.
/// </summary>
internal static class HlslSourceSplitter
{
    /// <summary>The diagnostic code for a source structure this frontend does not take.</summary>
    internal const string UnsupportedStructureCode = "SD0666";

    private static readonly Regex Identifier = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);

    private static readonly Regex ResourceType = new(
        @"^(?:RW)?(?:[A-Za-z]*Texture\w*|[A-Za-z]*Buffer\w*|[Ss]ampler\w*|ConstantBuffer|Cubemap\w*)$",
        RegexOptions.Compiled);

    // Template argument lists of resource types and vector/matrix, whose commas are not declarator
    // separators: Texture2DMS<float4, 4>, vector<float, 3>.
    private static readonly Regex TemplateArguments = new(
        @"\b(?:vector|matrix|[A-Za-z]*(?:Texture|Buffer)\w*)\s*<[\w\s,]*>", RegexOptions.Compiled);

    private static readonly HashSet<string> Qualifiers =
    [
        "static", "const", "uniform", "extern", "shared", "volatile", "precise", "nointerpolation",
        "row_major", "column_major", "groupshared", "linear", "centroid", "noperspective", "sample",
    ];

    /// <summary>Splits <paramref name="source"/>, or returns the structural errors found.</summary>
    /// <param name="source">The source text, with <c>\n</c> line endings.</param>
    /// <param name="fileName">The logical file name for diagnostics.</param>
    public static Result<List<HlslDeclaration>, ShaderError[]> Split(string source, string fileName)
    {
        string masked = SlangSourceMask.Mask(source);
        var declarations = new List<HlslDeclaration>();
        int[] lineStarts = LineStarts(source);
        int n = masked.Length;
        int i = 0;

        while (true)
        {
            while (i < n && char.IsWhiteSpace(masked[i]))
                i++;
            if (i >= n)
                break;

            int start = i;
            if (masked[i] == '#')
            {
                Result<int, ShaderError[]> end = ReadDirectiveUnit(masked, source, fileName, lineStarts, start, out HlslDeclarationKind kind,
                    out string name);
                if (end.IsFailure)
                    return Result<List<HlslDeclaration>, ShaderError[]>.Fail(end.Error);

                declarations.Add(Make(source, masked, lineStarts, start, end.Value, kind,
                    name.Length > 0 ? [name] : []));
                i = end.Value;
                continue;
            }

            Result<(int End, bool HasBody), ShaderError[]> unit = ReadDeclaration(masked, fileName, lineStarts, start);
            if (unit.IsFailure)
                return Result<List<HlslDeclaration>, ShaderError[]>.Fail(unit.Error);

            (int endIndex, bool hasBody) = unit.Value;
            string text = masked[start..endIndex];
            (HlslDeclarationKind declKind, List<string> names) = Classify(text, hasBody);
            declarations.Add(Make(source, masked, lineStarts, start, endIndex, declKind, names));
            i = endIndex;
        }

        return Result<List<HlslDeclaration>, ShaderError[]>.Ok(declarations);
    }

    private static HlslDeclaration Make(
        string source, string masked, int[] lineStarts, int start, int end, HlslDeclarationKind kind,
        IReadOnlyList<string> names)
    {
        string text = source[start..end].TrimEnd('\n', ' ', '\t');
        (int line, int column) = Position(lineStarts, start);
        return new HlslDeclaration(kind, names, text, Normalize(source[start..end]), line, column);
    }

    // ---- Preprocessor units ----------------------------------------------------------------------

    private static Result<int, ShaderError[]> ReadDirectiveUnit(
        string masked, string source, string fileName, int[] lineStarts, int start,
        out HlslDeclarationKind kind, out string name)
    {
        kind = HlslDeclarationKind.Directive;
        name = "";

        (string directive, int lineEnd) = ReadDirectiveLine(masked, start);

        switch (directive)
        {
            case "define":
                kind = HlslDeclarationKind.Macro;
                Match macro = Regex.Match(masked[start..lineEnd], @"#\s*define\s+([A-Za-z_]\w*)");
                name = macro.Success ? macro.Groups[1].Value : "";
                return Result<int, ShaderError[]>.Ok(lineEnd);

            case "if" or "ifdef" or "ifndef":
                kind = HlslDeclarationKind.Conditional;
                int depth = 1;
                int pos = lineEnd;
                while (pos < masked.Length && depth > 0)
                {
                    while (pos < masked.Length && char.IsWhiteSpace(masked[pos]))
                        pos++;
                    if (pos >= masked.Length)
                        break;

                    if (masked[pos] == '#')
                    {
                        (string inner, int innerEnd) = ReadDirectiveLine(masked, pos);
                        if (inner is "if" or "ifdef" or "ifndef")
                            depth++;
                        else if (inner == "endif")
                            depth--;
                        pos = innerEnd;
                    }
                    else
                    {
                        // Ordinary code inside the block: skip to the end of its line.
                        while (pos < masked.Length && masked[pos] != '\n')
                            pos++;
                    }
                }

                if (depth != 0)
                {
                    return Result<int, ShaderError[]>.Fail([Error(fileName, lineStarts, start,
                        $"A top-level #{directive} block is never closed by #endif. Close every conditional " +
                        "inside the source that opens it.")]);
                }

                return Result<int, ShaderError[]>.Ok(pos);

            case "else" or "elif" or "endif":
                return Result<int, ShaderError[]>.Fail([Error(fileName, lineStarts, start,
                    $"#{directive} has no matching #if at the top level of this source. A conditional block " +
                    "must be opened and closed inside the same source.")]);

            default:
                return Result<int, ShaderError[]>.Ok(lineEnd);
        }
    }

    /// <summary>Reads one logical directive line (backslash continuations joined); returns its name and end.</summary>
    private static (string Directive, int End) ReadDirectiveLine(string masked, int start)
    {
        int pos = start + 1;
        while (pos < masked.Length && masked[pos] is ' ' or '\t')
            pos++;
        int nameStart = pos;
        while (pos < masked.Length && (char.IsLetter(masked[pos]) || masked[pos] == '_'))
            pos++;
        string directive = masked[nameStart..pos];

        while (pos < masked.Length)
        {
            if (masked[pos] == '\n')
            {
                int back = pos - 1;
                while (back >= start && masked[back] == '\r')
                    back--;
                if (back >= start && masked[back] == '\\')
                {
                    pos++;
                    continue;
                }

                break;
            }

            pos++;
        }

        return (directive, pos);
    }

    // ---- Ordinary declarations -------------------------------------------------------------------

    private static Result<(int End, bool HasBody), ShaderError[]> ReadDeclaration(
        string masked, string fileName, int[] lineStarts, int start)
    {
        int n = masked.Length;
        int paren = 0, bracket = 0, brace = 0;
        bool hasBody = false;
        bool structBody = StartsWithStructBody(masked, start);
        int j = start;

        while (j < n)
        {
            char c = masked[j];

            // A preprocessor line inside a declaration (a #if inside a function body): skip it whole so
            // its text cannot be mistaken for code.
            if (c == '#' && IsFirstOnLine(masked, j))
            {
                (_, int end) = ReadDirectiveLine(masked, j);
                j = end;
                continue;
            }

            switch (c)
            {
                case '(': paren++; break;
                case ')': paren--; break;
                case '[': bracket++; break;
                case ']': bracket--; break;
                case '{':
                    brace++;
                    break;
                case '}':
                    brace--;
                    if (brace == 0 && paren == 0 && bracket == 0)
                    {
                        hasBody = true;
                        if (structBody)
                            break; // a struct ends at its ';', possibly after a declarator
                        int after = j + 1;
                        int look = after;
                        while (look < n && masked[look] is ' ' or '\t' or '\r' or '\n')
                            look++;
                        if (look < n && masked[look] == ';')
                            after = look + 1;
                        return Result<(int, bool), ShaderError[]>.Ok((after, true));
                    }

                    break;
                case ';':
                    if (paren == 0 && bracket == 0 && brace == 0)
                        return Result<(int, bool), ShaderError[]>.Ok((j + 1, hasBody));
                    break;
            }

            j++;
        }

        return Result<(int, bool), ShaderError[]>.Fail([Error(fileName, lineStarts, start,
            "This declaration is not closed: the source ends before its closing brace or semicolon.")]);
    }

    private static bool StartsWithStructBody(string masked, int start)
    {
        Match m = Regex.Match(masked[start..Math.Min(masked.Length, start + 256)], @"^(?:struct|class)\s+[A-Za-z_]\w*\s*(?::[^{;]*)?\{");
        return m.Success;
    }

    private static bool IsFirstOnLine(string masked, int index)
    {
        for (int k = index - 1; k >= 0 && masked[k] != '\n'; k--)
        {
            if (masked[k] is not (' ' or '\t' or '\r'))
                return false;
        }

        return true;
    }

    // ---- Classification --------------------------------------------------------------------------

    private static (HlslDeclarationKind Kind, List<string> Names) Classify(string masked, bool hasBody)
    {
        string text = masked.Trim();
        Match first = Identifier.Match(text);
        string firstWord = first.Success ? first.Value : "";

        if (firstWord is "struct" or "class" && first.Index == 0)
            return (HlslDeclarationKind.Struct, NameAfter(text, first));

        if (firstWord is "cbuffer" or "tbuffer" && first.Index == 0)
            return (HlslDeclarationKind.ConstantBuffer, NameAfter(text, first));

        if (firstWord == "typedef" && first.Index == 0)
            return (HlslDeclarationKind.Typedef, LastIdentifierNames(text));

        if (firstWord.StartsWith("technique", StringComparison.Ordinal) && first.Index == 0)
            return (HlslDeclarationKind.Technique, NameAfter(text, first));

        int paren = FirstAtBracketDepthZero(text, '(');
        int brace = text.IndexOf('{');
        int eq = text.IndexOf('=');
        int colon = text.IndexOf(':');
        bool functionLike = paren >= 0
                            && (brace < 0 || paren < brace)
                            && (eq < 0 || paren < eq)
                            && (colon < 0 || paren < colon);
        if (functionLike)
        {
            string head = text[..paren].TrimEnd();
            Match nameMatch = Regex.Match(head, @"([A-Za-z_]\w*)$");
            // Something must precede the name (a return type): "foo(...)" alone is not a declaration.
            if (nameMatch.Success && head[..nameMatch.Index].Trim().Length > 0)
            {
                return (hasBody ? HlslDeclarationKind.Function : HlslDeclarationKind.Prototype,
                    [nameMatch.Groups[1].Value]);
            }
        }

        // Everything else is a global: a resource, a uniform parameter, or a static/const constant.
        List<string> names = DeclaratorNames(text);
        var words = new List<string>();
        foreach (Match w in Identifier.Matches(StripBrackets(text)))
        {
            if (Qualifiers.Contains(w.Value))
                words.Add(w.Value);
            else
                break;
        }

        string typeWord = TypeWord(text);
        bool isResource = ResourceType.IsMatch(typeWord);
        bool isConstant = words.Contains("static") || (words.Contains("const") && !words.Contains("uniform"));

        HlslDeclarationKind kind = isResource
            ? HlslDeclarationKind.Resource
            : isConstant ? HlslDeclarationKind.Constant : HlslDeclarationKind.Parameter;
        return (kind, names);
    }

    private static List<string> NameAfter(string text, Match keyword)
    {
        Match name = Identifier.Match(text, keyword.Index + keyword.Length);
        return name.Success ? [name.Value] : [];
    }

    private static List<string> LastIdentifierNames(string text)
    {
        string body = text.TrimEnd(';', ' ', '\t', '\n', '\r');
        Match m = Regex.Match(body, @"([A-Za-z_]\w*)\s*(?:\[[^\]]*\])*$");
        return m.Success ? [m.Groups[1].Value] : [];
    }

    private static string TypeWord(string text)
    {
        foreach (Match w in Identifier.Matches(StripBrackets(text)))
        {
            if (!Qualifiers.Contains(w.Value))
                return w.Value;
        }

        return "";
    }

    /// <summary>Removes attribute lists and array brackets so they do not look like words.</summary>
    private static string StripBrackets(string text) => Regex.Replace(text, @"\[[^\]]*\]", " ");

    private static int FirstAtBracketDepthZero(string text, char target)
    {
        int depth = 0;
        for (int k = 0; k < text.Length; k++)
        {
            if (text[k] == '[') depth++;
            else if (text[k] == ']') depth--;
            else if (text[k] == target && depth == 0) return k;
        }

        return -1;
    }

    /// <summary>The names a global declaration declares (several for <c>float a, b;</c>).</summary>
    private static List<string> DeclaratorNames(string text)
    {
        string body = text.TrimEnd(';', ' ', '\t', '\n', '\r');
        // Collapse template argument lists so their commas are not read as declarator separators.
        body = TemplateArguments.Replace(body, m => Regex.Match(m.Value, @"^\w+").Value);

        var segments = new List<string>();
        int depth = 0, last = 0;
        for (int k = 0; k < body.Length; k++)
        {
            char c = body[k];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                segments.Add(body[last..k]);
                last = k + 1;
            }
        }

        segments.Add(body[last..]);

        var names = new List<string>();
        foreach (string segment in segments)
        {
            // The name is the last identifier before an initializer, semantic, register or array size.
            int cut = segment.Length;
            foreach (char stop in new[] { '=', ':', '[', '{' })
            {
                int at = segment.IndexOf(stop);
                if (at >= 0 && at < cut)
                    cut = at;
            }

            Match m = Regex.Match(segment[..cut], @"([A-Za-z_]\w*)\s*$");
            if (m.Success)
                names.Add(m.Groups[1].Value);
        }

        return names;
    }

    // ---- Text helpers ----------------------------------------------------------------------------

    /// <summary>The text with comments removed and whitespace collapsed, for equality of declarations.</summary>
    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                sb.Append(' ');
                continue;
            }

            if (c == '/' && next == '*')
            {
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                    i++;
                i = Math.Min(text.Length, i + 2);
                sb.Append(' ');
                continue;
            }

            if (c == '"')
            {
                sb.Append(c);
                i++;
                while (i < text.Length && text[i] != '"' && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                        sb.Append(text[i++]);
                    sb.Append(text[i++]);
                }

                if (i < text.Length && text[i] == '"')
                {
                    sb.Append('"');
                    i++;
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    internal static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int k = 0; k < text.Length; k++)
        {
            if (text[k] == '\n')
                starts.Add(k + 1);
        }

        return [.. starts];
    }

    internal static (int Line, int Column) Position(int[] lineStarts, int index)
    {
        int lo = 0, hi = lineStarts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= index)
                lo = mid;
            else
                hi = mid - 1;
        }

        return (lo + 1, index - lineStarts[lo] + 1);
    }

    private static ShaderError Error(string fileName, int[] lineStarts, int index, string message)
    {
        (int line, int column) = Position(lineStarts, index);
        return new ShaderError(File: fileName, Line: line, Column: column, Code: UnsupportedStructureCode,
            Message: message);
    }
}
