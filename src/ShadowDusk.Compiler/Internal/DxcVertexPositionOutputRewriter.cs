#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// A <b>DirectX 12 + Vulkan only</b> HLSL source rewrite (issue #446) that maps a vertex
/// shader's legacy <c>: POSITION</c> / <c>: POSITION0</c> OUTPUT semantic to <c>: SV_Position</c>,
/// so DXC (the shader-model 6 frontend of both targets) emits the system-value position the
/// rasterizer needs.
///
/// <para><b>Why.</b> <c>struct VOut { float4 Pos : POSITION0; ... }</c> returned by the vertex
/// shader is the standard XNA/MonoGame spelling. fxc (and so <c>mgfxc</c> on DirectX 11) and
/// vkd3d-shader (ShadowDusk's DirectX 11 backend, <c>MapSemanticNames</c>) treat a vertex-stage
/// output <c>POSITION</c> as <c>SV_Position</c> on SM4+. DXC has no such mapping: the vertex
/// shader gets a user <c>POSITION</c> output and no position at all, so MonoGame's WindowsDX12
/// throws creating the pipeline and DesktopVK silently draws nothing.</para>
///
/// <para><b>What is rewritten.</b> For every technique vertex entry, at each of its top-level
/// declarations (every <c>#if</c> branch too; the dead one never compiles):</para>
/// <list type="bullet">
/// <item>the return semantic (<c>float4 VS(...) : POSITION</c>);</item>
/// <item>each <c>out</c> parameter's semantic (<c>out float4 p : POSITION0</c>);</item>
/// <item>the members of each struct the entry returns or writes through an <c>out</c> parameter,
/// at every top-level definition of that struct. A pixel shader taking the same struct then
/// reads <c>SV_Position</c>, as on DirectX 11.</item>
/// </list>
///
/// <para><b>What is never touched.</b> Vertex <i>inputs</i> (the <c>POSITION0</c> vertex
/// attribute MonoGame's input layout names): an <c>in</c>/<c>inout</c> parameter, and any struct
/// some vertex entry also takes as an input (rewriting the shared definition would rename the
/// attribute). <c>POSITION1</c> and up (a second interpolant, never the position). A declaration
/// site that already writes <c>SV_Position</c> (two positions would be a DXC error). Pixel-shader
/// inputs not shared with a vertex output (vkd3d does not map a pixel-input <c>POSITION</c>
/// either).</para>
///
/// <para><b>Byte safety.</b> The rewrite runs downstream of the shared pre-parse, on a private
/// copy fed only to the DirectX 12 and Vulkan DXC compiles (like
/// <see cref="GlStructOutputColorRewriter"/> for OpenGL), so DirectX 11, OpenGL, KNIFX and FNA
/// bytes cannot move. It returns the input reference unchanged when nothing matches. It only
/// replaces a semantic token on its own line, so line numbers (diagnostics, the Vulkan dead-line
/// view) are unchanged.</para>
/// </summary>
internal static class DxcVertexPositionOutputRewriter
{
    private const string Replacement = "SV_Position";

    // A whole semantic token, case-insensitive like every HLSL semantic: POSITION or POSITION0
    // (never POSITION1+, POSITIONT, or a longer identifier).
    private static readonly Regex PositionSemantic =
        new(@"^POSITION0?$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ': POSITION' / ': POSITION0' inside a struct body.
    private static readonly Regex PositionMemberSemantic =
        new(@"(:\s*)POSITION0?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SvPositionSemantic =
        new(@":\s*SV_Position\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ParamModifiers = new(StringComparer.Ordinal)
    {
        "in", "out", "inout", "uniform", "const", "linear", "centroid", "nointerpolation",
        "noperspective", "sample", "precise", "row_major", "column_major", "snorm", "unorm",
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "return", "if", "else", "for", "while", "do", "switch", "case",
    };

    /// <summary>
    /// Returns <paramref name="hlsl"/> with every technique vertex entry's <c>POSITION</c> /
    /// <c>POSITION0</c> output semantic rewritten to <c>SV_Position</c>, or the same reference when
    /// there is none.
    /// </summary>
    public static string Rewrite(string hlsl, IReadOnlyList<TechniqueInfo> techniques)
    {
        var vertexEntries = new SortedSet<string>(StringComparer.Ordinal);
        foreach (TechniqueInfo tech in techniques)
            foreach (PassInfo pass in tech.Passes)
                if (!string.IsNullOrEmpty(pass.VertexEntryPoint))
                    vertexEntries.Add(pass.VertexEntryPoint!);
        if (vertexEntries.Count == 0)
            return hlsl;

        var edits = new List<(int Start, int Length)>();
        var outputTypes = new HashSet<string>(StringComparer.Ordinal);
        var inputTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (string entry in vertexEntries)
            foreach (int nameStart in FindTopLevelDeclarations(hlsl, entry))
                ScanEntrySignature(hlsl, nameStart, entry, edits, outputTypes, inputTypes);

        // A struct a vertex entry also takes as an INPUT carries the vertex attribute names
        // MonoGame's input layout binds; never rewrite its shared definition.
        outputTypes.ExceptWith(inputTypes);
        foreach (string structName in outputTypes)
            foreach ((int open, int close) in FindTopLevelStructBodies(hlsl, structName))
            {
                string body = hlsl.Substring(open, close - open + 1);
                if (SvPositionSemantic.IsMatch(body))
                    continue; // already writes the system value; a second one is a DXC error
                foreach (Match m in PositionMemberSemantic.Matches(body))
                {
                    int tokenStart = open + m.Index + m.Groups[1].Length;
                    edits.Add((tokenStart, m.Length - m.Groups[1].Length));
                }
            }

        if (edits.Count == 0)
            return hlsl;

        // Apply back to front so earlier offsets stay valid; a site reached twice (the same
        // declaration through two entries) is replaced once.
        string result = hlsl;
        foreach ((int start, int length) in edits.Distinct().OrderByDescending(e => e.Start))
            result = string.Concat(result.AsSpan(0, start), Replacement, result.AsSpan(start + length));
        return result;
    }

    /// <summary>Reads one declaration of a vertex entry: records the return / <c>out</c>
    /// semantic edits and the struct types used for output and for input.</summary>
    private static void ScanEntrySignature(
        string s, int nameStart, string entry,
        List<(int, int)> edits, HashSet<string> outputTypes, HashSet<string> inputTypes)
    {
        string? returnType = PreviousIdentifier(s, nameStart);
        int open = SkipWsAndComments(s, nameStart + entry.Length);
        int close = MatchParen(s, open);
        if (close < 0)
            return;

        var siteEdits = new List<(int, int)>();
        bool writesSvPosition = false;

        foreach ((int pStart, int pEnd) in SplitParameters(s, open + 1, close))
        {
            var words = new List<string>();
            int colon = -1;
            int i = pStart;
            while (i < pEnd)
            {
                i = SkipWsAndComments(s, i);
                if (i >= pEnd) break;
                char c = s[i];
                if (c == ':') { colon = i; break; }
                if (c == '=') break; // default value: no semantic follows
                if (IsIdentStart(c))
                {
                    int w = i;
                    while (i < pEnd && IsIdentChar(s[i])) i++;
                    words.Add(s.Substring(w, i - w));
                    continue;
                }
                i++;
            }

            bool isOut = words.Contains("out");
            bool isIn = !isOut || words.Contains("inout");
            if (words.Contains("inout"))
                isOut = true;
            string? type = words.FirstOrDefault(w => !ParamModifiers.Contains(w));

            if (type is not null)
            {
                if (isIn) inputTypes.Add(type);
                if (isOut) outputTypes.Add(type);
            }

            if (colon >= 0 && isOut)
            {
                (int semStart, int semLength) = SemanticAt(s, colon, pEnd);
                if (semLength > 0)
                {
                    string semantic = s.Substring(semStart, semLength);
                    if (semantic.Equals(Replacement, StringComparison.OrdinalIgnoreCase))
                        writesSvPosition = true;
                    else if (!isIn && PositionSemantic.IsMatch(semantic))
                        siteEdits.Add((semStart, semLength));
                }
            }
        }

        if (returnType is not null)
            outputTypes.Add(returnType);

        int after = SkipWsAndComments(s, close + 1);
        if (after < s.Length && s[after] == ':')
        {
            (int semStart, int semLength) = SemanticAt(s, after, s.Length);
            if (semLength > 0)
            {
                string semantic = s.Substring(semStart, semLength);
                if (semantic.Equals(Replacement, StringComparison.OrdinalIgnoreCase))
                    writesSvPosition = true;
                else if (PositionSemantic.IsMatch(semantic))
                    siteEdits.Add((semStart, semLength));
            }
        }

        if (!writesSvPosition)
            edits.AddRange(siteEdits);
    }

    /// <summary>The identifier after the <c>:</c> at <paramref name="colon"/>, as (start, length);
    /// length 0 when none.</summary>
    private static (int Start, int Length) SemanticAt(string s, int colon, int limit)
    {
        int i = SkipWsAndComments(s, colon + 1);
        if (i >= limit || !IsIdentStart(s[i]))
            return (i, 0);
        int start = i;
        while (i < limit && IsIdentChar(s[i])) i++;
        return (start, i - start);
    }

    /// <summary>The parameter ranges between <paramref name="start"/> and <paramref name="end"/>
    /// (exclusive), split on top-level commas (not inside <c>()</c>, <c>[]</c> or <c>&lt;&gt;</c>).</summary>
    private static IEnumerable<(int Start, int End)> SplitParameters(string s, int start, int end)
    {
        int depth = 0;
        int from = start;
        int i = start;
        while (i < end)
        {
            int next = SkipCommentAt(s, i, out bool skipped);
            if (skipped) { i = next; continue; }
            char c = s[i];
            if (c is '(' or '[' or '<') depth++;
            else if (c is ')' or ']' or '>') { if (depth > 0) depth--; }
            else if (c == ',' && depth == 0)
            {
                yield return (from, i);
                from = i + 1;
            }
            i++;
        }
        yield return (from, end);
    }

    /// <summary>Offsets of every top-level (brace depth 0) declaration of <paramref name="entry"/>:
    /// the name followed by <c>(</c> and preceded by a type identifier that is not a keyword.</summary>
    private static List<int> FindTopLevelDeclarations(string s, string entry)
    {
        var found = new List<int>();
        int depth = 0;
        int i = 0;
        while (i < s.Length)
        {
            int next = SkipCommentAt(s, i, out bool skipped);
            if (skipped) { i = next; continue; }
            char c = s[i];
            if (c == '{') { depth++; i++; continue; }
            if (c == '}') { if (depth > 0) depth--; i++; continue; }
            if (c == '"') { i = SkipString(s, i); continue; }
            if (IsIdentStart(c))
            {
                int w = i;
                while (i < s.Length && IsIdentChar(s[i])) i++;
                if (depth == 0 && i - w == entry.Length && string.CompareOrdinal(s, w, entry, 0, entry.Length) == 0)
                {
                    int after = SkipWsAndComments(s, i);
                    string? prev = PreviousIdentifier(s, w);
                    if (after < s.Length && s[after] == '(' && prev is not null && !Keywords.Contains(prev))
                        found.Add(w);
                }
                continue;
            }
            i++;
        }
        return found;
    }

    /// <summary>(open brace, close brace) of every top-level <c>struct Name { ... }</c>.</summary>
    private static List<(int Open, int Close)> FindTopLevelStructBodies(string s, string structName)
    {
        var bodies = new List<(int, int)>();
        int depth = 0;
        int i = 0;
        while (i < s.Length)
        {
            int next = SkipCommentAt(s, i, out bool skipped);
            if (skipped) { i = next; continue; }
            char c = s[i];
            if (c == '{') { depth++; i++; continue; }
            if (c == '}') { if (depth > 0) depth--; i++; continue; }
            if (c == '"') { i = SkipString(s, i); continue; }
            if (IsIdentStart(c))
            {
                int w = i;
                while (i < s.Length && IsIdentChar(s[i])) i++;
                if (depth == 0 && i - w == 6 && string.CompareOrdinal(s, w, "struct", 0, 6) == 0)
                {
                    int nameAt = SkipWsAndComments(s, i);
                    if (MatchesWordAt(s, nameAt, structName))
                    {
                        int brace = SkipWsAndComments(s, nameAt + structName.Length);
                        if (brace < s.Length && s[brace] == '{')
                        {
                            int close = MatchBrace(s, brace);
                            if (close > 0)
                            {
                                bodies.Add((brace, close));
                                i = close + 1;
                            }
                        }
                    }
                }
                continue;
            }
            i++;
        }
        return bodies;
    }

    // -- Low-level scanning helpers (comment-aware). -----------------------------------------

    private static int SkipCommentAt(string s, int i, out bool skipped)
    {
        skipped = false;
        if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/')
        {
            int nl = s.IndexOf('\n', i);
            skipped = true;
            return nl < 0 ? s.Length : nl;
        }
        if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
        {
            int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
            skipped = true;
            return end < 0 ? s.Length : end + 2;
        }
        return i;
    }

    private static int SkipWsAndComments(string s, int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            int next = SkipCommentAt(s, i, out bool skipped);
            if (!skipped) return i;
            i = next;
        }
        return i;
    }

    private static int SkipString(string s, int i)
    {
        i++;
        while (i < s.Length && s[i] != '"' && s[i] != '\n')
            i += s[i] == '\\' ? 2 : 1;
        return Math.Min(i + 1, s.Length);
    }

    private static int MatchParen(string s, int open) => MatchPair(s, open, '(', ')');

    private static int MatchBrace(string s, int open) => MatchPair(s, open, '{', '}');

    /// <summary>The index closing the <paramref name="openCh"/> at <paramref name="open"/>,
    /// skipping comments; -1 if unbalanced or <paramref name="open"/> is not that character.</summary>
    private static int MatchPair(string s, int open, char openCh, char closeCh)
    {
        if (open >= s.Length || s[open] != openCh)
            return -1;
        int depth = 0;
        int i = open;
        while (i < s.Length)
        {
            int next = SkipCommentAt(s, i, out bool skipped);
            if (skipped) { i = next; continue; }
            if (s[i] == openCh) depth++;
            else if (s[i] == closeCh && --depth == 0) return i;
            i++;
        }
        return -1;
    }

    /// <summary>The identifier ending just before <paramref name="pos"/> (whitespace skipped), or
    /// <c>null</c> when the preceding significant token is not an identifier.</summary>
    private static string? PreviousIdentifier(string s, int pos)
    {
        int i = pos - 1;
        while (i >= 0)
        {
            if (char.IsWhiteSpace(s[i])) { i--; continue; }
            if (i >= 1 && s[i] == '/' && s[i - 1] == '*')
            {
                // a block comment between the type and the name: skip back to its opening
                int opening = s.LastIndexOf("/*", i - 1, StringComparison.Ordinal);
                if (opening < 0) return null;
                i = opening - 1;
                continue;
            }
            break;
        }
        if (i < 0 || !IsIdentChar(s[i]))
            return null;
        int end = i + 1;
        while (i >= 0 && IsIdentChar(s[i])) i--;
        int start = i + 1;
        return IsIdentStart(s[start]) ? s.Substring(start, end - start) : null;
    }

    private static bool MatchesWordAt(string s, int i, string word)
    {
        if (i + word.Length > s.Length) return false;
        if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
        if (i > 0 && IsIdentChar(s[i - 1])) return false;
        int after = i + word.Length;
        return after >= s.Length || !IsIdentChar(s[after]);
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
