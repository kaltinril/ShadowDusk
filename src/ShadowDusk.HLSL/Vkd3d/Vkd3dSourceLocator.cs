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
/// against the pinned vkd3d 2.1 (<c>plan/DONE/ISSUE-202-fna-error-line-numbers.md</c> §3):</para>
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
/// exists. Every probe also ends with <see cref="Terminator"/>, so it stops at the parse even
/// when its sentinel sits where vkd3d never sees it (a skipped <c>#if</c> arm, a block
/// comment); without that such a probe is the whole failing compile over again.</para>
///
/// <para><b>The raw text.</b> A failing compile can say thousands of located lines, all
/// printed under the summary, and each needs the same relocation. Those are not bisected one
/// by one: a single compile carrying a statement marker on every statement start
/// (<see cref="MarkerFor"/>) measures the whole file at once, and the bisection only runs
/// inside the few brackets the markers leave open.</para>
///
/// <para><b>Columns.</b> vkd3d reports the column in its OWN re-spaced token stream (every
/// token separated by one space, indentation dropped: <c>int x=;</c> and
/// <c>    int   x   =   ;</c> both report column 9). The column therefore identifies the
/// <i>k</i>-th token of the line; the <i>k</i>-th token of the author's line has a known
/// source column. When the tokens do not align (a macro-expanded line) the column is left as
/// vkd3d reported it.</para>
///
/// <para>Constraint 5 is untouched: <see cref="ShaderError.Message"/>, <see cref="ShaderError.Code"/>,
/// and <see cref="ShaderError.Severity"/> stay verbatim; only <c>File</c>/<c>Line</c>/<c>Column</c>
/// move. In <see cref="ShaderError.RawDiagnostics"/> the same holds per line: each
/// <c>file:line:col:</c> prefix naming the compiled source moves the same way (the blob is
/// printed under the summary, so vkd3d's coordinates there contradicted it), and every
/// character after the prefix stays as vkd3d wrote it.</para>
/// </summary>
internal static partial class Vkd3dSourceLocator
{
    /// <summary>
    /// The sentinel line: vkd3d's HLSL lexer has no rule for <c>@</c>, so the parser sees
    /// its "invalid token" and aborts in EVERY syntactic position (top level, function body,
    /// expression, struct body, parameter list, initializer) — measured on vkd3d 2.1. Bison
    /// spells that token two ways depending on the version that generated the parser (see
    /// <see cref="SentinelMessage"/> and <see cref="SentinelMessageLegacyBison"/>).
    /// </summary>
    internal const string Sentinel = "@";

    /// <summary>
    /// The diagnostic text vkd3d 2.1 emits for the sentinel (code E5000) when its parser was
    /// generated by bison 3.6 or newer (the win-x64 MinGW build and both macOS Homebrew builds).
    /// </summary>
    internal const string SentinelMessage = "syntax error, unexpected invalid token";

    /// <summary>
    /// The SAME diagnostic from a parser generated by bison older than 3.6, which names the
    /// undefined token <c>$undefined</c> instead of "invalid token": the linux-x64 build comes
    /// out of an Ubuntu 20.04 container (bison 3.5.1) for its glibc 2.31 baseline. The
    /// spelling is a property of the bison that generated <c>hlsl.tab.c</c>, not of the vkd3d
    /// version, so every host must recognise both or the bisection never brackets on that host
    /// (it did not, on Linux: 128 probes, then the raw coordinates, until this was measured).
    /// </summary>
    internal const string SentinelMessageLegacyBison = "syntax error, unexpected $undefined";

    /// <summary>
    /// Probe budget per diagnostic. A bisection over a 3 000-line effect needs about 12 probes;
    /// skipped-arm scans add a few more. Exhausting the budget leaves the diagnostic exactly as
    /// vkd3d reported it rather than guessing.
    /// </summary>
    internal const int MaxProbes = 128;

    /// <summary>
    /// Extra probe allowance for relocating the located lines of the raw diagnostic blob
    /// (<see cref="RelocateRawDiagnostics"/>), on top of what the summary spent. The marker
    /// compiles count against it. When it runs out before every line is placed, the whole
    /// blob keeps vkd3d's coordinates: a block mixing the two would be worse than either.
    /// </summary>
    internal const int MaxRawProbes = 64;

    /// <summary>
    /// Appended as the last line of EVERY probe. A probe must end at the parse, about a
    /// hundredth of a real compile; without this, a sentinel that is swallowed (planted in a
    /// skipped <c>#if</c> arm or a block comment, where vkd3d never sees it) lets the probe
    /// run the WHOLE failing compile again. Measured on the reporter's Apos.Shapes file: one
    /// such probe, in the <c>#if VULKAN</c> arm, cost as much as the compile itself and
    /// doubled the time to the error. An unbalanced <c>)</c> is a syntax error wherever the
    /// source ends, and deliberately NOT the sentinel's token: a probe that ends here must
    /// still read as "the sentinel did not fire".
    /// </summary>
    internal const string Terminator = ")";

    /// <summary>How many marker compiles one relocation may spend (each drops one misplaced marker).</summary>
    internal const int MaxMarkerCompiles = 4;

    private const string MarkerPrefix = "__shadowdusk_line_";

    /// <summary>
    /// The statement marker for physical line <paramref name="line"/>: an empty <c>if</c>
    /// carrying an attribute vkd3d does not know. vkd3d answers with a non-fatal located
    /// warning that quotes the attribute's name (<c>W5302: Unrecognized attribute '…'</c>)
    /// and parses on, so one compile can carry one marker per statement. It is prefixed to
    /// the line, never inserted as a line of its own, so it moves no line number; and the
    /// warning's line is the line vkd3d would report for a diagnostic at the start of that
    /// physical line, exactly what a sentinel planted before it reports.
    /// </summary>
    internal static string MarkerFor(int line) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[{MarkerPrefix}{line}__] if(0){{}} ");

    [GeneratedRegex(MarkerPrefix + @"(?<s>\d+)__", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerName();

    // vkd3d's own diagnostic line: "<file>:<line>:<col>: <code>: <message>". The file part
    // can carry a drive letter, so it anchors on the first ":<digits>:<digits>:".
    [GeneratedRegex(@"^(?<file>.+?):(?<line>\d+):(?<col>\d+):(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RawLocatedLine();

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
    /// <param name="cancellationToken">Checked before EVERY probe, here rather than in each
    /// host's probe delegate so the desktop and browser hosts cannot drift (issue #255). A
    /// probe is one uninterruptible native compile and a diagnostic may need up to
    /// <see cref="MaxProbes"/> of them, so a cancelled compile stops paying for them at the
    /// next probe boundary with an <see cref="OperationCanceledException"/>.</param>
    public static IReadOnlyList<ShaderError> Relocate(
        IReadOnlyList<ShaderError> diagnostics,
        string vkd3dSource,
        string originalSource,
        string sourceFileName,
        Func<string, ShaderError?> probe,
        CancellationToken cancellationToken = default)
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

            session ??= new ProbeSession(vkd3dSource, probe, cancellationToken);
            result[i] = RelocateCore(session, d, originalSource, sourceFileName);
        }
        return result;
    }

    /// <summary>Single-diagnostic form of <see cref="Relocate(IReadOnlyList{ShaderError}, string, string, string, Func{string, ShaderError?}, CancellationToken)"/>.</summary>
    public static ShaderError Relocate(
        ShaderError diagnostic,
        string vkd3dSource,
        string originalSource,
        string sourceFileName,
        Func<string, ShaderError?> probe,
        CancellationToken cancellationToken = default)
    {
        if (!IsLocated(diagnostic, sourceFileName))
            return diagnostic;
        return RelocateCore(
            new ProbeSession(vkd3dSource, probe, cancellationToken), diagnostic, originalSource, sourceFileName);
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
        var relocated = d with { File = file, Line = line, Column = column };

        return relocated with
        {
            RawDiagnostics = RelocateRawDiagnostics(session, d, relocated, originalSource, sourceFileName),
        };
    }

    /// <summary>
    /// Moves the <c>file:line:col:</c> prefix of every vkd3d diagnostic line in
    /// <see cref="ShaderError.RawDiagnostics"/> onto the author's source, exactly as the
    /// summary's own location moved; everything after the prefix (code and text) stays
    /// verbatim. The delivery surfaces print that blob under the relocated summary whenever
    /// vkd3d said more than one line, so without this they still showed vkd3d's coordinates:
    /// on the reporter's Apos.Shapes file the summary said line 983 and the block under it
    /// said 1115 for the same diagnostic, with later lines up to 3804 in a 3235-line file,
    /// which is the symptom issue #202 reported.
    /// <para>
    /// All or nothing: when even one line naming the compiled source cannot be placed, the
    /// blob is returned exactly as vkd3d wrote it. Lines naming another file and lines that
    /// carry no location are never touched.
    /// </para>
    /// <para>
    /// Cost. The blob can hold thousands of lines (4 165 on the reporter's file, 934
    /// distinct locations), so it cannot afford a bisection each. One marker compile
    /// (<see cref="ProbeSession.MeasureStatementStarts"/>) measures every statement start at
    /// once; a line between two measurements is then inferred from them, and only a line
    /// they cannot pin (a later line of a multi-line statement that calls a template
    /// intrinsic, code outside a function body) is bisected, inside that small bracket.
    /// </para>
    /// </summary>
    private static string? RelocateRawDiagnostics(
        ProbeSession session, ShaderError original, ShaderError relocated, string originalSource, string sourceFileName)
    {
        string? raw = original.RawDiagnostics;
        if (string.IsNullOrEmpty(raw))
            return raw;

        var placed = new Dictionary<(int Line, int Column), (string File, int Line, int Column)>
        {
            [(original.Line, original.Column)] = (relocated.File, relocated.Line, relocated.Column),
        };
        (string File, int Line)[]? lineTable = null;

        string[] lines = raw.Split('\n');
        var rewritten = new string?[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            string text = lines[i];
            bool cr = text.EndsWith('\r');
            Match m = RawLocatedLine().Match(cr ? text[..^1] : text);
            if (!m.Success || !string.Equals(m.Groups["file"].Value, sourceFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            int reportedLine = int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
            int reportedColumn = int.Parse(m.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (reportedLine <= 0)
                continue;
            string rest = m.Groups["rest"].Value;

            if (!placed.TryGetValue((reportedLine, reportedColumn), out var target))
            {
                if (lineTable is null)
                {
                    // The first line that is not the summary's own: only now is there work
                    // to pay for. An author's own '@' makes a fired sentinel ambiguous, so
                    // that one case keeps to plain bisection.
                    session.ExtendBudget(MaxRawProbes);
                    if (!ProbeSession.IsSentinelShaped(original))
                        session.MeasureStatementStarts();
                    lineTable = BuildLineTable(originalSource, sourceFileName);
                }

                var located = new ShaderError(sourceFileName, reportedLine, reportedColumn, string.Empty, rest);
                int? physical = session.LocatePhysicalLine(
                    located, useMeasurements: !ProbeSession.IsSentinelShaped(original));
                if (physical is not { } p || p >= lineTable.Length)
                    return raw;

                target = (lineTable[p].File, lineTable[p].Line, RemapColumn(session.LineText(p), reportedColumn));
                placed[(reportedLine, reportedColumn)] = target;
            }

            string moved = string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"{target.File}:{target.Line}:{target.Column}:{rest}");
            rewritten[i] = cr ? moved + "\r" : moved;
        }

        for (int i = 0; i < lines.Length; i++)
            lines[i] = rewritten[i] ?? lines[i];
        return string.Join('\n', lines);
    }

    /// <summary>
    /// <see cref="ResolveLineDirectives"/> for every physical line at once: entry <i>p</i> is
    /// the author's (file, line) of physical line <i>p</i> (entry 0 is unused). The raw blob
    /// resolves hundreds of lines, and replaying the directives from the top for each one is
    /// quadratic.
    /// </summary>
    internal static (string File, int Line)[] BuildLineTable(string text, string defaultFile)
    {
        string[] lines = text.Split('\n');
        var table = new (string File, int Line)[lines.Length + 1];
        string file = defaultFile;
        int line = 1;
        for (int p = 1; p <= lines.Length; p++)
        {
            table[p] = (file, line);
            Match m = LineDirective().Match(lines[p - 1]);
            if (m.Success)
            {
                line = int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (m.Groups["f"].Success && m.Groups["f"].Value.Length > 0)
                {
                    // Same rule as ResolveLineDirectives: a slash-only difference from the
                    // name the compiler was given collapses back onto that spelling.
                    string named = m.Groups["f"].Value;
                    file = string.Equals(named.Replace('\\', '/'), defaultFile.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                        ? defaultFile
                        : named;
                }
            }
            else
            {
                line++;
            }
        }
        return table;
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
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<int, ShaderError?> _memo = new();
        private readonly Dictionary<int, int> _marked = new();     // physical line -> the line vkd3d reports for its start
        private int[] _markedLines = [];                           // the same, sorted by physical line
        private int[] _markedReported = [];
        private int[]? _irregularBefore;
        private bool _markersTried;
        private int _probes;
        private int _budget = MaxProbes;

        public ProbeSession(string vkd3dSource, Func<string, ShaderError?> probe, CancellationToken cancellationToken)
        {
            _lines = vkd3dSource.Split('\n');
            _probe = probe;
            _cancellationToken = cancellationToken;
        }

        public string LineText(int physicalLine) =>
            physicalLine >= 1 && physicalLine <= _lines.Length ? _lines[physicalLine - 1].TrimEnd('\r') : string.Empty;

        // ---------------------------------------------------------------------
        // What is already measured, and what follows from it without a probe
        // ---------------------------------------------------------------------

        /// <summary>
        /// The line vkd3d reports for the START of physical line <paramref name="line"/>, when a
        /// statement marker or a sentinel that fired there has already measured it.
        /// </summary>
        private bool TryKnownReported(int line, out int reported)
        {
            if (_marked.TryGetValue(line, out reported))
                return true;
            if (_memo.TryGetValue(line, out ShaderError? fired) && fired is not null && IsSentinelShaped(fired))
            {
                reported = fired.Line;
                return true;
            }
            reported = 0;
            return false;
        }

        /// <summary>
        /// True when no line in <c>[lo, hi]</c> can LOWER vkd3d's count: no directive (a
        /// skipped arm), no block-comment delimiter (a collapsed interior), no continuation.
        /// Inside such a stretch the drift only ever grows, at template-intrinsic call sites.
        /// </summary>
        private bool IsCountPreserving(int lo, int hi)
        {
            if (_irregularBefore is null)
            {
                var prefix = new int[_lines.Length + 1];
                for (int i = 0; i < _lines.Length; i++)
                {
                    string text = _lines[i].TrimEnd('\r');
                    bool irregular = text.TrimStart().StartsWith('#') || text.EndsWith('\\')
                        || text.Contains("/*", StringComparison.Ordinal) || text.Contains("*/", StringComparison.Ordinal);
                    prefix[i + 1] = prefix[i] + (irregular ? 1 : 0);
                }
                _irregularBefore = prefix;
            }
            return _irregularBefore[hi] - _irregularBefore[lo - 1] == 0;
        }

        /// <summary>
        /// Answers without probing when the bracket's two ends are measured and the stretch
        /// between them preserves vkd3d's count. A diagnostic on line <i>s</i> of the bracket
        /// was lexed with a drift between the drift at the bracket's start and the drift at
        /// its end, so <c>reported - driftAtEnd &lt;= s &lt;= reported - driftAtStart</c>; and a
        /// diagnostic always sits on a token, so a blank or comment-only line cannot be it. When
        /// exactly one line of the bracket qualifies, that is the line the bisection would
        /// converge on. Equal drift at both ends (no template call in between) is the common
        /// case and leaves one line by construction.
        /// </summary>
        private int? TryInferFromBracket(int lo, int hi, int reportedLine)
        {
            if (!TryKnownReported(lo, out int reportedLo) || !TryKnownReported(hi + 1, out int reportedEnd))
                return null;

            int driftLo = reportedLo - lo;
            int driftEnd = reportedEnd - (hi + 1);
            if (driftEnd < driftLo || !IsCountPreserving(lo, hi))
                return null;

            int? answer = null;
            for (int s = Math.Max(lo, reportedLine - driftEnd); s <= Math.Min(hi, reportedLine - driftLo); s++)
            {
                if (!Tokenize(LineText(s)).Any())
                    continue;
                if (answer is not null)
                    return null;    // two lines qualify: only a probe can tell them apart
                answer = s;
            }
            return answer;
        }

        /// <summary>Allows <paramref name="probes"/> more probes beyond those already spent.</summary>
        public void ExtendBudget(int probes) => _budget = Math.Max(_budget, _probes + probes);

        /// <summary>
        /// The physical (flattened-text) line the diagnostic sits on, or <see langword="null"/>
        /// when the probe budget ran out before the search converged.
        /// </summary>
        /// <param name="d">The diagnostic, in vkd3d's coordinates.</param>
        /// <param name="useMeasurements">Start from the tightest bracket the measurements
        /// already taken give, and infer the line from it where that is exact.</param>
        public int? LocatePhysicalLine(ShaderError d, bool useMeasurements = false)
        {
            int lo = 1;
            int hi = _lines.Length;
            bool anchored = false;      // lo was established by a sentinel that actually fired
            bool originalIsSentinelShaped = IsSentinelShaped(d);
            bool narrowFromMemo = useMeasurements && !originalIsSentinelShaped;

            // A measurement at or before the diagnostic raises lo, one after it lowers hi.
            // These are the verdicts the bisection would reach itself, without paying again.
            if (narrowFromMemo)
            {
                int after = UpperBound(_markedReported, d.Line);    // first marker reporting past the diagnostic
                if (after > 0) { lo = _markedLines[after - 1]; anchored = true; }
                if (after < _markedLines.Length) hi = _markedLines[after] - 1;

                foreach ((int s, ShaderError? r) in _memo)
                {
                    if (r is null || !IsSentinelShaped(r))
                        continue;
                    if (r.Line <= d.Line)
                    {
                        if (s >= lo) { lo = s; anchored = true; }
                    }
                    else if (s - 1 < hi)
                    {
                        hi = s - 1;
                    }
                }
                if (lo > hi)
                    return null;    // the measurements disagree with each other: leave it as reported
            }

            while (lo < hi)
            {
                if (narrowFromMemo && TryInferFromBracket(lo, hi, d.Line) is { } inferred)
                    return inferred;

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

        public static bool IsSentinelShaped(ShaderError e) =>
            e.Message.Contains(SentinelMessage, StringComparison.Ordinal)
            || e.Message.Contains(SentinelMessageLegacyBison, StringComparison.Ordinal);

        private Verdict? Classify(int line, ShaderError d, bool originalIsSentinelShaped)
        {
            // A sentinel after a backslash-continued line would be spliced INTO the directive
            // above it (a macro body), corrupting the very text being measured. Never plant one there.
            if (line >= 2 && _lines[line - 2].TrimEnd('\r').EndsWith('\\'))
                return Verdict.Unknown;

            if (!_memo.TryGetValue(line, out ShaderError? result))
            {
                if (_probes >= _budget)
                    return null;
                // The one cancellation point of the relocation: a probe is a whole native
                // compile that nothing can interrupt once it has started.
                _cancellationToken.ThrowIfCancellationRequested();
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
            var sb = new StringBuilder(_lines.Sum(l => l.Length + 1) + Sentinel.Length + Terminator.Length + 2);
            for (int i = 0; i < _lines.Length; i++)
            {
                if (i == line - 1)
                    sb.Append(Sentinel).Append('\n');
                sb.Append(_lines[i]);
                if (i < _lines.Length - 1)
                    sb.Append('\n');
            }
            return sb.Append('\n').Append(Terminator).ToString();
        }

        // ---------------------------------------------------------------------
        // Statement markers: many measurements from one parse
        // ---------------------------------------------------------------------

        /// <summary>
        /// Measures the reported line of every statement start in ONE parse-abort compile, by
        /// prefixing each such line with <see cref="MarkerFor"/>: vkd3d answers each marker
        /// with a non-fatal located warning that names it, and the <see cref="Terminator"/>
        /// ends the compile once the parse is through. A bisection pays one compile per
        /// halving per diagnostic; this pays one compile for the whole file, which is what
        /// makes a raw block of thousands of lines affordable.
        /// <para>
        /// Where a statement starts is decided by <see cref="StatementStartLines"/>, a
        /// heuristic. It cannot produce a wrong measurement, only a missing one: a marker that
        /// is not legal where it was put is a syntax error, the parse stops there, and the
        /// first marker that did not answer is dropped before the next attempt (at most
        /// <see cref="MaxMarkerCompiles"/>). Lines left unmeasured fall back to the bisection.
        /// </para>
        /// </summary>
        public void MeasureStatementStarts()
        {
            if (_markersTried)
                return;
            _markersTried = true;

            List<int> candidates = StatementStartLines(_lines);
            for (int attempt = 0; attempt < MaxMarkerCompiles && candidates.Count > 0 && _probes < _budget; attempt++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _probes++;
                string? answer = _probe(WithMarkers(candidates))?.RawDiagnostics;
                if (string.IsNullOrEmpty(answer))
                    break;

                foreach (string answerLine in answer.Split('\n'))
                {
                    Match located = RawLocatedLine().Match(answerLine);
                    if (!located.Success)
                        continue;
                    Match marker = MarkerName().Match(located.Groups["rest"].Value);
                    if (marker.Success)
                    {
                        _marked[int.Parse(marker.Groups["s"].Value, System.Globalization.CultureInfo.InvariantCulture)] =
                            int.Parse(located.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                // Every candidate sits in unconditional code, so each one answers unless the
                // parse stopped first: the first silent one is where it stopped.
                int silent = candidates.FindIndex(c => !_marked.ContainsKey(c));
                if (silent < 0)
                    break;
                candidates.RemoveRange(0, silent + 1);
            }

            // vkd3d's count never runs backwards. Measurements that say it did are not
            // measurements of what this code believes they are: use none of them.
            int[] lines = _marked.Keys.Order().ToArray();
            int[] reported = Array.ConvertAll(lines, l => _marked[l]);
            for (int i = 1; i < reported.Length; i++)
            {
                if (reported[i] < reported[i - 1])
                {
                    _marked.Clear();
                    return;
                }
            }
            _markedLines = lines;
            _markedReported = reported;
        }

        private string WithMarkers(List<int> markedLines)
        {
            var sb = new StringBuilder(_lines.Sum(l => l.Length + 1) + markedLines.Count * 48 + Terminator.Length + 1);
            int next = 0;
            for (int i = 0; i < _lines.Length; i++)
            {
                if (next < markedLines.Count && markedLines[next] == i + 1)
                {
                    sb.Append(MarkerFor(i + 1));
                    next++;
                }
                sb.Append(_lines[i]);
                if (i < _lines.Length - 1)
                    sb.Append('\n');
            }
            return sb.Append('\n').Append(Terminator).ToString();
        }

        /// <summary>The index of the first element greater than <paramref name="value"/> in an ascending array.</summary>
        private static int UpperBound(int[] ascending, int value)
        {
            int lo = 0;
            int hi = ascending.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (ascending[mid] <= value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }

    // -------------------------------------------------------------------------
    // Where a statement marker may go
    // -------------------------------------------------------------------------

    private enum Scope
    {
        /// <summary>A function body.</summary>
        Function,
        /// <summary>A compound statement inside a function body.</summary>
        Block,
        /// <summary>The body of a <c>do</c>: the <c>while</c> that follows it is not a statement start.</summary>
        DoBody,
        /// <summary>Anything a statement cannot appear in: a struct, a buffer, a technique, an initializer list.</summary>
        Other,
    }

    private static readonly HashSet<string> NonFunctionHeaders = new(StringComparer.Ordinal)
    {
        "struct", "class", "interface", "namespace", "typedef",
        "cbuffer", "tbuffer", "technique", "technique10", "technique11", "pass",
    };

    /// <summary>
    /// The 1-based lines whose first token starts a statement inside a function body: the
    /// places <see cref="MarkerFor"/> can be prefixed. A deliberately conservative reading of
    /// the source, not a parser: a line qualifies only when the code before it ended a
    /// statement or opened or closed a block (<c>;</c>, <c>{</c>, <c>}</c>) inside a function,
    /// outside every parenthesis, outside every <c>#if</c> arm (a marker in a skipped arm
    /// would never answer), and not where the next token continues the previous statement
    /// (<c>else</c>, <c>case</c>, <c>default</c>, the <c>while</c> of a <c>do</c>). A shape
    /// this misreads costs one extra compile, never a wrong line; see
    /// <see cref="ProbeSession.MeasureStatementStarts"/>.
    /// </summary>
    internal static List<int> StatementStartLines(IReadOnlyList<string> lines)
    {
        var result = new List<int>();
        var scopes = new Stack<Scope>();
        Scope lastClosed = Scope.Other;
        bool inBlockComment = false;
        bool directiveContinues = false;
        bool previousLineSpliced = false;
        int conditionalDepth = 0;
        int parenDepth = 0;
        string previous = string.Empty;         // the last code token before the current one
        string headerFirst = string.Empty;      // the first token of the top-level declaration in progress
        bool headerHasParen = false;
        bool headerHasAssign = false;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].TrimEnd('\r');
            bool spliced = previousLineSpliced;
            previousLineSpliced = line.EndsWith('\\');

            if (directiveContinues)
            {
                directiveContinues = previousLineSpliced;
                continue;
            }

            if (!inBlockComment && line.AsSpan().TrimStart().StartsWith("#"))
            {
                ReadOnlySpan<char> name = line.AsSpan().TrimStart()[1..].TrimStart();
                if (name.StartsWith("if"))
                    conditionalDepth++;
                else if (name.StartsWith("endif") && conditionalDepth > 0)
                    conditionalDepth--;
                directiveContinues = previousLineSpliced;
                continue;
            }

            bool startedInComment = inBlockComment;
            bool first = true;
            foreach ((int start, int length) in TokenizeCode(line, ref inBlockComment))
            {
                string token = line.Substring(start, length);
                if (first)
                {
                    first = false;
                    bool continuesPrevious = token is "else" or "case" or "default"
                        || (previous == "}" && (lastClosed == Scope.Other || (lastClosed == Scope.DoBody && token == "while")));
                    if (!startedInComment && !spliced && conditionalDepth == 0 && parenDepth == 0
                        && scopes.Count > 0 && scopes.Peek() != Scope.Other
                        && previous is ";" or "{" or "}" && !continuesPrevious)
                    {
                        result.Add(i + 1);
                    }
                }

                if (scopes.Count == 0 && headerFirst.Length == 0)
                    headerFirst = token;

                switch (token)
                {
                    case "(":
                        parenDepth++;
                        headerHasParen |= scopes.Count == 0;
                        break;
                    case ")":
                        if (parenDepth > 0) parenDepth--;
                        break;
                    case "=":
                        headerHasAssign |= scopes.Count == 0;
                        break;
                    case "{":
                        Scope scope;
                        if (scopes.Count == 0)
                        {
                            scope = headerHasParen && !headerHasAssign && parenDepth == 0 && !NonFunctionHeaders.Contains(headerFirst)
                                ? Scope.Function
                                : Scope.Other;
                        }
                        else if (scopes.Peek() == Scope.Other || parenDepth != 0)
                        {
                            scope = Scope.Other;
                        }
                        else
                        {
                            scope = previous switch
                            {
                                "do" => Scope.DoBody,
                                ")" or "else" or ";" or "{" or "}" or ":" => Scope.Block,
                                _ => Scope.Other,       // '= {', ', {': an initializer list
                            };
                        }
                        scopes.Push(scope);
                        break;
                    case "}":
                        if (scopes.Count > 0)
                            lastClosed = scopes.Pop();
                        if (scopes.Count == 0)
                        {
                            headerFirst = string.Empty;
                            headerHasParen = headerHasAssign = false;
                            parenDepth = 0;
                        }
                        break;
                    case ";":
                        if (scopes.Count == 0)
                        {
                            headerFirst = string.Empty;
                            headerHasParen = headerHasAssign = false;
                            parenDepth = 0;
                        }
                        break;
                }
                previous = token;
            }
        }
        return result;
    }

    /// <summary>
    /// <see cref="Tokenize"/> for one line of a multi-line text: skips what is still inside a
    /// block comment opened on an earlier line, and reports whether this line leaves one open.
    /// </summary>
    internal static List<(int Start, int Length)> TokenizeCode(string line, ref bool inBlockComment)
    {
        var tokens = new List<(int Start, int Length)>();
        int from = 0;
        if (inBlockComment)
        {
            int close = line.IndexOf("*/", StringComparison.Ordinal);
            if (close < 0)
                return tokens;
            from = close + 2;
            inBlockComment = false;
        }

        string rest = from == 0 ? line : line[from..];
        foreach ((int start, int length) in Tokenize(rest))
            tokens.Add((start + from, length));

        // Tokenize drops everything after a '/*' that does not close on the line; find out
        // whether that happened (outside a string, outside a '//' comment).
        for (int i = 0; i < rest.Length; i++)
        {
            char c = rest[i];
            if (c == '"')
            {
                i++;
                while (i < rest.Length && rest[i] != '"')
                    i += rest[i] == '\\' ? 2 : 1;
            }
            else if (c == '/' && i + 1 < rest.Length && rest[i + 1] == '/')
            {
                break;
            }
            else if (c == '/' && i + 1 < rest.Length && rest[i + 1] == '*')
            {
                int close = rest.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    inBlockComment = true;
                    break;
                }
                i = close + 1;
            }
        }
        return tokens;
    }
}
