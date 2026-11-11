#nullable enable

using System.Text;
using System.Text.RegularExpressions;

namespace ShadowDusk.GLSL;

/// <summary>
/// Rule 18 (issue #475): lowers every array-initializer declaration (<c>float[](a, b, c)</c>,
/// <c>float[3](a, b, c)</c>) to a plain array declaration plus one assignment per element.
/// The array constructor is GLSL 1.20 / ESSL 3.00; the versionless dialect the rewriter emits
/// (GLSL 1.10 / ESSL 1.00) has none, so WebGL1 and KNI Reach refuse the shader at Effect load
/// while lenient desktop drivers accept it. <c>mgfxc</c>'s output never contains one.
/// <para>
/// An HLSL array initializer reaches SPIRV-Cross's output in two places, and both are handled:
/// a module-level <c>const T name[N] = T[](…);</c> (DXC hoists a constant table), and a local
/// <c>T name[N] = T[](…);</c> inside a function. A local is declared and then assigned in place.
/// A module-level array has no statement position, so it is declared without its initializer and
/// assigned at the top of <c>main</c>; every other function runs from <c>main</c>, so they see
/// the filled array. The <c>const</c> qualifier is dropped (the array is now written after
/// declaration); the values and indexing are unchanged.
/// </para>
/// <para>
/// <b>All-or-nothing.</b> A shape the pass does not model (an array of arrays, an element count
/// that does not match the declared size, a module-level array read by another module-level
/// initializer, which would run before <c>main</c> fills it) makes the whole pass decline and
/// return the text unchanged, so the SD0403 portability warning (which looks for a surviving
/// array constructor) still fires. A shader with no array constructor is returned unchanged
/// (the same instance) without parsing.
/// </para>
/// </summary>
internal static class ArrayConstructorLowering
{
    // `T[](` or `T[N](`: the constructor spelling. Also used by GlslPortabilityAnalyzer.
    internal const string ConstructorPattern = @"\b[A-Za-z_]\w*\s*\[\s*\d*\s*\]\s*\(";

    private static readonly Regex AnyConstructor = new(ConstructorPattern, RegexOptions.Compiled);

    private static readonly Regex Declaration = new(
        @"^(?<indent>[ \t]*)(?:const\s+)?(?<prec>(?:highp|mediump|lowp)\s+)?(?<type>[A-Za-z_]\w*)\s+" +
        @"(?<name>[A-Za-z_]\w*)\s*\[\s*(?<n>\d*)\s*\]\s*=\s*" +
        @"(?<ctype>[A-Za-z_]\w*)\s*\[\s*(?<cn>\d*)\s*\]\s*\(",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex MainSignature = new(
        @"\bvoid\s+main\s*\(\s*(?:void\s*)?\)", RegexOptions.Compiled);

    /// <summary>
    /// Lowers the array constructors of <paramref name="body"/>, or returns it unchanged when it
    /// has none or when any of them is a shape the pass does not model.
    /// </summary>
    public static string Lower(string body)
    {
        if (!AnyConstructor.IsMatch(body))
        {
            return body;
        }
        return Run(body) ?? body;
    }

    private static string? Run(string body)
    {
        var output = new StringBuilder(body.Length + 256);
        var globalNames = new List<string>();
        var globalAssignments = new List<string>();
        int position = 0;

        foreach (Match m in Declaration.Matches(body))
        {
            if (m.Index < position)
            {
                return null; // overlaps a declaration already consumed: a nested constructor
            }
            if (m.Groups["type"].Value != m.Groups["ctype"].Value)
            {
                return null;
            }

            int open = m.Index + m.Length - 1;
            int close = FindMatchingParen(body, open);
            if (close < 0)
            {
                return null;
            }
            int semicolon = close + 1;
            while (semicolon < body.Length && char.IsWhiteSpace(body[semicolon]))
            {
                semicolon++;
            }
            if (semicolon >= body.Length || body[semicolon] != ';')
            {
                return null;
            }

            List<string>? elements = SplitTopLevel(body, open + 1, close);
            if (elements is null || elements.Count == 0 || elements.Any(e => AnyConstructor.IsMatch(e)))
            {
                return null;
            }

            int count = elements.Count;
            if (m.Groups["n"].Length > 0 && int.Parse(m.Groups["n"].Value) != count)
            {
                return null;
            }
            if (m.Groups["cn"].Length > 0 && int.Parse(m.Groups["cn"].Value) != count)
            {
                return null;
            }

            string indent = m.Groups["indent"].Value;
            string name = m.Groups["name"].Value;
            string declaration =
                $"{m.Groups["prec"].Value}{m.Groups["type"].Value} {name}[{count}];";

            output.Append(body, position, m.Index - position);
            if (DepthAt(body, m.Index) == 0)
            {
                // Module level: no statement position here, so main fills it in.
                output.Append(indent).Append(declaration);
                globalNames.Add(name);
                for (int i = 0; i < count; i++)
                {
                    globalAssignments.Add($"    {name}[{i}] = {elements[i]};");
                }
            }
            else
            {
                output.Append(indent).Append(declaration);
                for (int i = 0; i < count; i++)
                {
                    output.Append('\n').Append(indent).Append($"{name}[{i}] = {elements[i]};");
                }
            }
            position = semicolon + 1;
        }

        output.Append(body, position, body.Length - position);
        string result = output.ToString();

        // Anything still spelled as a constructor is a shape not modelled above (an array of
        // arrays, a constructor used as an expression): decline the whole pass.
        if (AnyConstructor.IsMatch(result))
        {
            return null;
        }

        if (globalNames.Count == 0)
        {
            return result;
        }

        // A module-level array read by another module-level statement (`const float k = t[1];`)
        // would be read before main fills it.
        string outsideFunctions = StripBracedBlocks(result);
        foreach (string name in globalNames)
        {
            if (Regex.Matches(outsideFunctions, $@"\b{Regex.Escape(name)}\b").Count != 1)
            {
                return null;
            }
        }

        Match main = MainSignature.Match(result);
        if (!main.Success)
        {
            return null;
        }
        int brace = result.IndexOf('{', main.Index + main.Length);
        if (brace < 0)
        {
            return null;
        }
        int insertAt = result.IndexOf('\n', brace);
        insertAt = insertAt < 0 ? brace + 1 : insertAt + 1;
        string block = string.Join("\n", globalAssignments) + "\n";
        return result.Insert(insertAt, block);
    }

    private static int DepthAt(string text, int index)
    {
        int depth = 0;
        for (int i = 0; i < index; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth--;
        }
        return depth;
    }

    /// <summary>The text with every <c>{ … }</c> block (function and struct bodies) removed.</summary>
    private static string StripBracedBlocks(string text)
    {
        var sb = new StringBuilder(text.Length);
        int depth = 0;
        foreach (char c in text)
        {
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }

    private static int FindMatchingParen(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Splits <c>text[start..end)</c> at commas that sit outside any parentheses or brackets.</summary>
    private static List<string>? SplitTopLevel(string text, int start, int end)
    {
        var parts = new List<string>();
        int depth = 0;
        int partStart = start;
        for (int i = start; i < end; i++)
        {
            char c = text[i];
            if (c == '(' || c == '[') depth++;
            else if (c == ')' || c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(text.Substring(partStart, i - partStart).Trim());
                partStart = i + 1;
            }
            if (depth < 0)
            {
                return null;
            }
        }
        parts.Add(text.Substring(partStart, end - partStart).Trim());
        return parts.Any(p => p.Length == 0) ? null : parts;
    }
}
