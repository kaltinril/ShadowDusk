#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Vkd3d;

/// <summary>
/// Puts a vkd3d-shader diagnostic back onto the author's file, line, and column
/// (GitHub issue #202). Pure: no I/O, no interop — the compile it needs is injected as a
/// probe delegate, so the desktop P/Invoke host and the browser <c>[JSImport]</c> host
/// relocate identically and the algorithm is unit-testable against a fake vkd3d.
///
/// <para><b>Why vkd3d's own coordinates are wrong.</b> Three independent drifts, all measured
/// against the pinned vkd3d 1.17 (<c>plan/DONE/ISSUE-202-fna-error-line-numbers.md</c> §3):</para>
/// <list type="number">
/// <item>ShadowDusk's preprocessor prepends a macro prelude and flattens includes, marking each
/// boundary with <c>#line</c>; vkd3d's preprocessor ignores <c>#line</c>, so the caller blanks
/// them (line-preserving) and vkd3d reports flattened-text coordinates.</item>
/// <item>vkd3d's preprocessor emits no line for a skipped <c>#if</c> arm and collapses a
/// multi-line block comment to one line, with no line markers to compensate: every later
/// line reports LOWER than it is.</item>
/// <item>vkd3d implements <c>atan2</c>, <c>asin</c>, <c>sincos</c>, <c>smoothstep</c>, … by lexing
/// an internal HLSL template through the same lexer, whose line counter lives on the shared
/// compiler context and is never restored (<c>hlsl.l</c> <c>++ctx-&gt;location.line</c>):
/// every call site of such an intrinsic pushes every later line HIGHER by the template's
/// height (+20 per <c>atan2</c>, +11 per <c>asin</c>, +4 per <c>sincos</c>, …), uncached and
/// data-dependent. The reporter's line 3009 came out as 3586.</item>
/// </list>
///
/// <para><b>Why the fix measures instead of modelling.</b> The GCC-style <c># N "file"</c>
/// marker vkd3d's HLSL lexer honours is grammatically legal only BETWEEN top-level
/// declarations, and the drift accumulates inside function bodies, so markers cannot make a
/// body-local diagnostic exact. A managed model of the drift would need vkd3d's preprocessed
/// text plus a per-intrinsic table pinned to its internals. Instead vkd3d is asked directly:
/// a sentinel line holding the lexer's "invalid token" (<c>@</c>) planted before physical line
/// <i>s</i> aborts the parse there and reports <i>s</i> + (all drift before <i>s</i>) — exactly
/// what a diagnostic on line <i>s</i> reports, with macros, skipped arms, comments, and
/// templates accounted for by the real compiler. Bisecting <i>s</i> until
/// <c>R(s) &lt;= L &lt; R(s+1)</c> recovers the physical line; the un-blanked text's
/// <c>#line</c> directives then give the author's file and line. A parse-abort probe costs
/// about a hundredth of the failing compile, and probes run only when a located diagnostic
/// exists.</para>
///
/// <para><b>Columns.</b> vkd3d reports the column in its OWN re-spaced token stream (every
/// token separated by one space, indentation dropped: <c>int x=;</c> and
/// <c>    int   x   =   ;</c> both report column 9). The column therefore identifies the
/// <i>k</i>-th token of the line; the <i>k</i>-th token of the author's line has a known
/// source column. When the tokens do not align (a macro-expanded line) the column is left as
/// vkd3d reported it.</para>
///
/// <para>Constraint 5 is untouched: <see cref="ShaderError.Message"/>, <see cref="ShaderError.Code"/>,
/// <see cref="ShaderError.Severity"/>, and <see cref="ShaderError.RawDiagnostics"/> stay
/// verbatim; only <c>File</c>/<c>Line</c>/<c>Column</c> move.</para>
/// </summary>
internal static partial class Vkd3dSourceLocator
{
    /// <summary>
    /// The sentinel line: vkd3d's HLSL lexer has no rule for <c>@</c>, so the parser sees
    /// its "invalid token" and aborts in EVERY syntactic position (top level, function body,
    /// expression, struct body, parameter list, initializer) — measured on vkd3d 1.17.
    /// </summary>
    internal const string Sentinel = "@";

    /// <summary>The diagnostic text vkd3d 1.17 emits for the sentinel (code E5000).</summary>
    internal const string SentinelMessage = "syntax error, unexpected invalid token";

    /// <summary>
    /// Probe budget per diagnostic. A bisection over a 3 000-line effect needs about 12 probes;
    /// skipped-arm scans add a few more. Exhausting the budget leaves the diagnostic exactly as
    /// vkd3d reported it rather than guessing.
    /// </summary>
    internal const int MaxProbes = 128;

    [GeneratedRegex(@"^[ \t]*#[ \t]*line[ \t]+(?<n>\d+)(?:[ \t]+""(?<f>[^""]*)"")?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex LineDirective();

    /// <summary>
    /// Relocates every located diagnostic (<c>Line &gt; 0</c>, named after the compile's
    /// source file) in <paramref name="diagnostics"/>. Unlocated entries and entries naming
    /// another file pass through untouched.
    /// </summary>
    /// <param name="diagnostics">vkd3d's diagnostics as parsed by the reformatter.</param>
    /// <param name="vkd3dSource">The exact text vkd3d compiled (the <c>#line</c>-blanked flattened source).</param>
    /// <param name="originalSource">The same text BEFORE blanking (carries the <c>#line</c> directives); identical line count.</param>
    /// <param name="sourceFileName">The name vkd3d was given for the source (what it reports in its diagnostics).</param>
    /// <param name="probe">Compiles the given text with the SAME request and returns the primary
    /// diagnostic (<see langword="null"/> when it compiled). The locator only ever passes
    /// <paramref name="vkd3dSource"/> with one sentinel line inserted.</param>
    public static IReadOnlyList<ShaderError> Relocate(
        IReadOnlyList<ShaderError> diagnostics,
        string vkd3dSource,
        string originalSource,
        string sourceFileName,
        Func<string, ShaderError?> probe)
    {
        if (diagnostics.Count == 0)
            return diagnostics;

        ProbeSession? session = null;
        var result = new ShaderError[diagnostics.Count];
        for (int i = 0; i < diagnostics.Count; i++)
        {
            ShaderError d = diagnostics[i];
            if (!IsLocated(d, sourceFileName))
            {
                result[i] = d;
                continue;
            }

            session ??= new ProbeSession(vkd3dSource, probe);
            result[i] = RelocateCore(session, d, originalSource, sourceFileName);
        }
        return result;
    }

    /// <summary>Single-diagnostic form of <see cref="Relocate(IReadOnlyList{ShaderError}, string, string, string, Func{string, ShaderError?})"/>.</summary>
    public static ShaderError Relocate(
        ShaderError diagnostic,
        string vkd3dSource,
        string originalSource,
        string sourceFileName,
        Func<string, ShaderError?> probe)
    {
        if (!IsLocated(diagnostic, sourceFileName))
            return diagnostic;
        return RelocateCore(new ProbeSession(vkd3dSource, probe), diagnostic, originalSource, sourceFileName);
    }

    private static bool IsLocated(ShaderError d, string sourceFileName) =>
        d.Line > 0 && string.Equals(d.File, sourceFileName, StringComparison.OrdinalIgnoreCase);

    private static ShaderError RelocateCore(
        ProbeSession session, ShaderError d, string originalSource, string sourceFileName)
    {
        int? physical = session.LocatePhysicalLine(d);
        if (physical is not { } physicalLine)
            return d;   // budget exhausted or nothing fired: honest raw coordinates beat a guess

        (string file, int line) = ResolveLineDirectives(originalSource, physicalLine, sourceFileName);
        int column = RemapColumn(session.LineText(physicalLine), d.Column);
        return d with { File = file, Line = line, Column = column };
    }

    // -------------------------------------------------------------------------
    // #line resolution (the caller's own directives, in the un-blanked text)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Maps a physical line of the flattened text to the author's (file, line) by replaying
    /// the <c>#line N "file"</c> directives above it. A directive names the line that
    /// FOLLOWS it. With no directive above, the file is <paramref name="defaultFile"/> and
    /// the line is physical.
    /// </summary>
    internal static (string File, int Line) ResolveLineDirectives(
        string text, int physicalLine, string defaultFile)
    {
        string file = defaultFile;
        int line = 1;
        int pos = 0;
        for (int i = 1; i < physicalLine; i++)
        {
            int newline = text.IndexOf('\n', pos);
            int end = newline < 0 ? text.Length : newline;
            if (pos > text.Length)
                break;

            Match m = LineDirective().Match(text, pos, end - pos);
            if (m.Success)
            {
                line = int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (m.Groups["f"].Success && m.Groups["f"].Value.Length > 0)
                    file = m.Groups["f"].Value;
            }
            else
            {
                line++;
            }

            if (newline < 0)
                break;
            pos = newline + 1;
        }

        // The prelude's own '#line 1 "<file>"' spells the path with forward slashes (MacroSet
        // normalizes it); a diagnostic on the entry file must still name the path EXACTLY as
        // the compiler was given it (the CLI/MGCB jump-to-file contract), so a slash-only
        // difference collapses back onto the request's spelling.
        if (!string.Equals(file, defaultFile, StringComparison.Ordinal)
            && string.Equals(file.Replace('\\', '/'), defaultFile.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            file = defaultFile;

        return (file, line);
    }

    // -------------------------------------------------------------------------
    // Column remap (vkd3d's re-spaced token column -> the author's column)
    // -------------------------------------------------------------------------

    /// <summary>
    /// vkd3d's preprocessor re-emits each line as its tokens joined by single spaces with the
    /// indentation dropped, and its diagnostics count columns in THAT text. The column thus
    /// names the k-th token; return the source column of the k-th token of
    /// <paramref name="sourceLine"/>, or <paramref name="vkd3dColumn"/> unchanged when no
    /// token starts at that re-spaced column (a macro-expanded line, or column 0).
    /// </summary>
    internal static int RemapColumn(string sourceLine, int vkd3dColumn)
    {
        if (vkd3dColumn <= 0 || sourceLine.Length == 0)
            return vkd3dColumn;

        int respacedColumn = 1;
        foreach ((int start, int length) in Tokenize(sourceLine))
        {
            if (respacedColumn == vkd3dColumn)
                return start + 1;
            if (respacedColumn > vkd3dColumn)
                break;
            respacedColumn += length + 1;
        }
        return vkd3dColumn;
    }

    /// <summary>
    /// Splits one source line the way vkd3d's preprocessor does for its output: identifiers,
    /// pp-numbers (<c>1e-6</c>, <c>0.5f</c>, <c>.5</c> stay whole), string literals, the C
    /// multi-character operators as one token each, every other character alone. Comments
    /// are dropped (a <c>/*</c> that does not close on the line swallows the rest).
    /// </summary>
    internal static IEnumerable<(int Start, int Length)> Tokenize(string line)
    {
        int i = 0;
        int n = line.Length;
        while (i < n)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '/' && i + 1 < n && line[i + 1] == '/')
                yield break;
            if (c == '/' && i + 1 < n && line[i + 1] == '*')
            {
                int close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) yield break;
                i = close + 2;
                continue;
            }

            int start = i;
            if (c == '"')
            {
                i++;
                while (i < n && line[i] != '"')
                    i += line[i] == '\\' ? 2 : 1;
                i = Math.Min(i + 1, n);
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                while (i < n && (char.IsAsciiLetterOrDigit(line[i]) || line[i] == '_')) i++;
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(line[i + 1])))
            {
                // C pp-number: [0-9.] then any of [0-9A-Za-z_.] or an exponent sign after e/E/p/P.
                i++;
                while (i < n)
                {
                    char d = line[i];
                    if (char.IsAsciiLetterOrDigit(d) || d == '_' || d == '.') { i++; continue; }
                    if ((d == '+' || d == '-') && i > start && line[i - 1] is 'e' or 'E' or 'p' or 'P') { i++; continue; }
                    break;
                }
            }
            else
            {
                i += OperatorLength(line, i);
            }
            yield return (start, i - start);
        }
    }

    private static int OperatorLength(string s, int i)
    {
        if (i + 2 < s.Length)
        {
            string three = s.Substring(i, 3);
            if (three is "<<=" or ">>=") return 3;
        }
        if (i + 1 < s.Length)
        {
            string two = s.Substring(i, 2);
            if (two is "<=" or ">=" or "==" or "!=" or "&&" or "||" or "++" or "--"
                    or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^="
                    or "<<" or ">>" or "::" or "->")
                return 2;
        }
        return 1;
    }

    // -------------------------------------------------------------------------
    // The bisection
    // -------------------------------------------------------------------------

    private enum Verdict
    {
        /// <summary>The sentinel fired and reported at or before the diagnostic: the diagnostic's line is at or after this one.</summary>
        AtOrBefore,
        /// <summary>The sentinel fired and reported after the diagnostic: the diagnostic's line is before this one.</summary>
        After,
        /// <summary>The sentinel did not fire here (swallowed by a comment or a skipped arm, or the parse had already aborted earlier).</summary>
        Unknown,
        /// <summary>Fired at exactly the diagnostic's own coordinates while the diagnostic itself IS a sentinel-shaped error (an <c>@</c> in the author's source): indistinguishable from the parse aborting on the author's token.</summary>
        Ambiguous,
    }

    private sealed class ProbeSession
    {
        private readonly string[] _lines;
        private readonly Func<string, ShaderError?> _probe;
        private readonly Dictionary<int, ShaderError?> _memo = new();
        private int _probes;

        public ProbeSession(string vkd3dSource, Func<string, ShaderError?> probe)
        {
            _lines = vkd3dSource.Split('\n');
            _probe = probe;
        }

        public string LineText(int physicalLine) =>
            physicalLine >= 1 && physicalLine <= _lines.Length ? _lines[physicalLine - 1].TrimEnd('\r') : string.Empty;

        /// <summary>
        /// The physical (flattened-text) line the diagnostic sits on, or <see langword="null"/>
        /// when the probe budget ran out before the search converged.
        /// </summary>
        public int? LocatePhysicalLine(ShaderError d)
        {
            int lo = 1;
            int hi = _lines.Length;
            bool anchored = false;      // lo was established by a sentinel that actually fired
            bool originalIsSentinelShaped = IsSentinelShaped(d);

            while (lo < hi)
            {
                int mid = lo + (hi - lo + 1) / 2;
                Verdict? v = Classify(mid, d, originalIsSentinelShaped);
                if (v is null)
                    return null;

                if (v == Verdict.AtOrBefore) { lo = mid; anchored = true; continue; }
                if (v == Verdict.After)      { hi = mid - 1; continue; }

                // Nothing fired before mid: that sentinel sat in a token-less stretch (a
                // block-comment interior, a skipped #if arm, a spliced continuation). Such a
                // stretch ends at a boundary line — one holding '*/', a directive, or the last
                // backslash-continued line — and the line after the boundary is where a
                // sentinel can fire again. Walk boundary to boundary, never blindly: a blind
                // stride could jump over the one code line between two comment blocks.
                bool resolved = false;
                int from = mid;
                while (true)
                {
                    int candidate = NextCandidateAfterBoundary(from, hi);
                    if (candidate < 0)
                        break;
                    Verdict? w = Classify(candidate, d, originalIsSentinelShaped);
                    if (w is null)
                        return null;
                    if (w == Verdict.AtOrBefore) { lo = candidate; anchored = true; resolved = true; break; }
                    if (w == Verdict.After)      { hi = candidate - 1; resolved = true; break; }
                    from = candidate;
                }
                if (!resolved)
                    hi = mid - 1;   // every line from mid to hi is token-less: the diagnostic is before mid
            }

            // An author's own '@' at column 1: probes at its line and beyond all look like the
            // original, so the search settles one line early. A probe exactly one past the
            // answer that is ambiguous can only mean the answer is that next line.
            if (originalIsSentinelShaped && lo + 1 <= _lines.Length
                && Classify(lo + 1, d, originalIsSentinelShaped) == Verdict.Ambiguous)
                return lo + 1;

            // A line nothing ever fired at is not an answer: confirm it, or give the
            // diagnostic back as vkd3d reported it.
            if (!anchored && Classify(lo, d, originalIsSentinelShaped) != Verdict.AtOrBefore)
                return null;

            return lo;
        }

        /// <summary>
        /// The first line after <paramref name="from"/> (inclusive of its own boundary) that
        /// follows a boundary line — a line containing <c>*/</c> or starting with <c>#</c> —
        /// skipping past any backslash continuation, capped at <paramref name="hi"/>; -1 when
        /// there is none left (then <paramref name="hi"/> itself is offered once).
        /// </summary>
        private int NextCandidateAfterBoundary(int from, int hi)
        {
            for (int b = from; b <= hi; b++)
            {
                string line = _lines[b - 1].TrimEnd('\r');
                string trimmed = line.TrimStart();
                bool boundary = line.Contains("*/", StringComparison.Ordinal) || trimmed.StartsWith('#');
                if (!boundary)
                    continue;

                int candidate = b + 1;
                while (candidate <= hi && _lines[candidate - 2].TrimEnd('\r').EndsWith('\\'))
                    candidate++;
                if (candidate > from && candidate <= hi)
                    return candidate;
            }
            return from < hi ? hi : -1;
        }

        private static bool IsSentinelShaped(ShaderError e) =>
            e.Message.Contains(SentinelMessage, StringComparison.Ordinal);

        private Verdict? Classify(int line, ShaderError d, bool originalIsSentinelShaped)
        {
            // A sentinel after a backslash-continued line would be spliced INTO the directive
            // above it (a macro body), corrupting the very text being measured. Never plant one there.
            if (line >= 2 && _lines[line - 2].TrimEnd('\r').EndsWith('\\'))
                return Verdict.Unknown;

            if (!_memo.TryGetValue(line, out ShaderError? result))
            {
                if (_probes >= MaxProbes)
                    return null;
                _probes++;
                result = _probe(WithSentinelBefore(line));
                _memo[line] = result;
            }

            if (result is null || !IsSentinelShaped(result))
                return Verdict.Unknown;

            if (originalIsSentinelShaped && result.Line == d.Line && result.Column == d.Column)
                return Verdict.Ambiguous;

            return result.Line <= d.Line ? Verdict.AtOrBefore : Verdict.After;
        }

        private string WithSentinelBefore(int line)
        {
            var sb = new StringBuilder(_lines.Sum(l => l.Length + 1) + Sentinel.Length + 1);
            for (int i = 0; i < _lines.Length; i++)
            {
                if (i == line - 1)
                    sb.Append(Sentinel).Append('\n');
                sb.Append(_lines[i]);
                if (i < _lines.Length - 1)
                    sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
