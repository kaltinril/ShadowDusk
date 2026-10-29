#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Preprocessing;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Keeps an entry point's prototypes out of a DXC SPIR-V compile, because DXC's SPIR-V back end
/// crashes the host process on them.
/// </summary>
/// <remarks>
/// <para><b>The native fault.</b> The pinned DXC (Vortice.Dxc 3.3.4's <c>dxcompiler</c>) faults
/// with an access violation inside <c>IDxcCompiler3::Compile</c> whenever <c>-spirv</c> is
/// requested and the ENTRY function is declared more than once: a prototype before the
/// definition, after it, or several, with or without semantics. A prototype of any other
/// function is fine, and the same source compiles to DXIL (<c>-Vd</c>) without complaint. The
/// fault is not catchable in .NET, so it took the whole process down: a consumer's game, the
/// MGCB build, the test host. That is the OpenGL and Vulkan targets, and the raylib and SkSL
/// converters. A preprocess-only run (<c>-P</c>) never reaches the back end and does not fault,
/// whatever the source declares.</para>
///
/// <para><b>Why removing the prototype is faithful.</b> DXC's own DXIL path takes the entry's
/// semantics from the definition and never from a prototype, and fxc (<c>mgfxc</c> 3.8.4.1)
/// compiles these shapes. So a prototype whose return type and parameter types match the single
/// definition, and that says nothing the definition does not, is blanked from the compile's
/// input: replaced by spaces (as many as its UTF-8 bytes), keeping every line break, so no
/// diagnostic line or column moves. DXC then sees one declaration and compiles the definition it
/// would have compiled. A prototype that says MORE than its definition (an attribute, a leading
/// modifier such as <c>precise</c>, <c>inline</c> or <c>static</c>, a parameter's interpolation
/// modifier or default value) is refused instead, because DXC's front end may merge that into the
/// definition (and answers an <c>inline</c>/<c>static</c> entry with "cannot find entry
/// function"), so removing it could change the shader without a word. An overload prototype
/// (different parameters, no definition of its own) of an entry function nothing calls is inert,
/// fxc compiles past it, and it is blanked too.</para>
///
/// <para><b>DXC's own preprocessor decides, and proves.</b> DXC compiles the text after its
/// preprocessor, so every decision is made on what DXC's preprocessor itself produces: a
/// <c>-P</c> run with the compile's own arguments (its <c>-D</c> macros, <c>-T</c> profile and
/// <c>-spirv</c>, so <c>__spirv__</c>, <c>__SHADER_TARGET_*</c> and every macro DXC predefines take
/// the values the compile sees). Nothing here models DXC's preprocessor (its conditionals, its
/// arithmetic, its predefined macros). When that text declares the entry once, the source goes to
/// DXC unchanged, so a prototype in a branch DXC does not compile changes nothing. When it
/// declares it more than once, ranges of the RAW source are chosen to blank (between exact raw
/// token boundaries on the prototype's first and last lines, found through DXC's <c>#line</c>
/// markers), and each is accepted only when DXC's preprocessor, run again on the source with that
/// range blanked, produces exactly the original token stream minus the prototype's tokens (see
/// <see cref="Locate"/>). So a macro call before the prototype on its line
/// (<c>DECL_PARAM(Unused) float4 PS(...);</c>) stays, a prototype a macro completes
/// (<c>: POS_OUT</c>) or writes whole (<c>DECLARE(PS)</c>) is removed exactly, and one no range can
/// remove alone (a macro writing it with other code) is refused. A source DXC's preprocessor
/// rejects is handed to DXC unchanged: the compile fails on the same diagnostic, in the front end,
/// before the back end runs.</para>
///
/// <para><b>Fail-safe.</b> The crash kills the consumer's process, so no redeclared entry reaches
/// the SPIR-V back end. Two or more declaration-shaped uses of the entry name at global scope in
/// DXC's preprocessed text, any of which cannot be read as a plain declaration (a parenthesised
/// declarator, <c>float4 (PS)(...)</c>, among them), are refused with <c>SD0236</c>. So is a call to
/// the entry function between its prototype and its definition (without the prototype the call
/// has no declaration; fxc compiles it), an overload prototype of an entry function the source
/// calls, a prototype whose return type is spelled differently from the definition's in a way that
/// cannot be told apart from DXC's own type identity, a declaration shape the reading does not
/// model (a second declarator, <c>float4 A(), PS(...);</c>), and a prototype no proven range
/// removes. No definition or several, and a prototype whose return type is a different BUILT-IN
/// type, reach DXC, whose front end reports them cleanly (<c>missing entry point definition</c>,
/// <c>ambiguous entry point function</c>, <c>functions that differ only in their return type</c>)
/// before the back end runs.</para>
///
/// <para>The validated DXIL of DirectX 12 and the DXBC of DirectX 11 and FNA never had the
/// fault and keep their input as is (see <see cref="AppliesTo"/>). The input is changed only
/// when a prototype of the entry point is blanked; otherwise it is handed to DXC as it was (or
/// refused, which happens only where DXC's preprocessed text declares the entry twice).</para>
/// </remarks>
internal static partial class DxcSpirvEntryRedeclarations
{
    /// <summary>The registered diagnostic code for an entry-point redeclaration that cannot be removed safely.</summary>
    public const string ErrorCode = "SD0236";

    /// <summary>
    /// True for a DXC compile that emits SPIR-V, the only output with the fault, and for an
    /// unvalidated (<c>-Vd</c>) DXIL compile: that is the OpenGL target's reflection companion,
    /// whose blob is discarded and which should read the same text as the SPIR-V compile it
    /// describes (otherwise an unused overload prototype fxc accepts compiles to SPIR-V and is
    /// "ambiguous" to the companion). Shipped DXIL (DirectX 12) is always validated, so it never
    /// comes through here. A preprocess-only run (<c>-P</c>) never reaches a back end.
    /// </summary>
    public static bool AppliesTo(IReadOnlyList<string> arguments) =>
        !arguments.Contains("-P", StringComparer.Ordinal)
        && (arguments.Contains("-spirv", StringComparer.Ordinal) || arguments.Contains("-Vd", StringComparer.Ordinal));

    /// <summary>True when the compile with <paramref name="arguments"/> reaches DXC's SPIR-V back end (the one with the fault).</summary>
    public static bool EmitsSpirv(IReadOnlyList<string> arguments) => arguments.Contains("-spirv", StringComparer.Ordinal);

    /// <summary>
    /// The arguments of a DXC preprocess-only run that sees exactly what the compile with
    /// <paramref name="compileArguments"/> sees: the same <c>-D</c> macros, profile, <c>-spirv</c>
    /// and input name (so every macro DXC predefines, <c>__FILE__</c> included, takes the value the
    /// compile gives it), without the code-generation and validation switches no preprocessor
    /// reads, plus <c>-P</c>.
    /// </summary>
    public static IReadOnlyList<string> PreprocessArguments(IReadOnlyList<string> compileArguments)
    {
        var arguments = new List<string>(compileArguments.Count + 1);
        foreach (string argument in compileArguments)
        {
            if (argument is "-Zi" or "-Qembed_debug" or "-Vd")
                continue;
            arguments.Add(argument);
        }
        arguments.Add("-P");
        return arguments;
    }

    /// <summary>What a DXC preprocess-only run produced.</summary>
    /// <param name="Succeeded">False when DXC's preprocessor reported an error (the compile fails on it too, before the back end).</param>
    /// <param name="Text">The preprocessed text, with DXC's <c>#line</c> markers.</param>
    internal readonly record struct PreprocessedText(bool Succeeded, string Text);

    /// <summary>
    /// The text DXC's SPIR-V compile of <paramref name="entryPoint"/> should be handed: the
    /// source unchanged, the source with the entry function's redundant prototypes blanked, or
    /// an <see cref="ErrorCode"/> refusal.
    /// </summary>
    /// <param name="source">The HLSL compiler input, exactly as it would be handed to DXC.</param>
    /// <param name="entryPoint">The entry function DXC is asked to compile (DXC's <c>-E</c>).</param>
    /// <param name="sourceFileName">Display name used in diagnostics.</param>
    /// <param name="macros">The <c>-D</c> macros the compile passes to DXC (only read by the cheap
    /// gate that skips the preprocess run when the entry's name cannot reach DXC twice).</param>
    /// <param name="preprocess">
    /// Runs DXC's preprocessor on a text with the compile's own arguments
    /// (<see cref="PreprocessArguments"/>): the only judge of what DXC compiles.
    /// </param>
    public static Result<string, ShaderError> Prepare(
        string source,
        string entryPoint,
        string? sourceFileName,
        IReadOnlyList<(string Name, string? Value)>? macros,
        Func<string, PreprocessedText> preprocess)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(preprocess);
        string file = sourceFileName ?? string.Empty;

        if (string.IsNullOrEmpty(entryPoint) || CannotBeDeclaredTwice(source, entryPoint, macros ?? []))
            return Ok(source);

        PreprocessedText original = preprocess(source);
        if (!original.Succeeded)
            return Ok(source);

        Analysis analysis = Analyze(original.Text, entryPoint);
        if (analysis.DeclarationShaped < 2)
            return Ok(source);

        var locator = new Locator(source, original.Text, file, entryPoint);
        if (analysis.Unread.Count > 0)
            return Fail(locator.Fix(Unread(original.Text, file, entryPoint, analysis.Unread[0])));

        Result<IReadOnlyList<Declaration>, ShaderError> plan = Plan(analysis, original.Text, file, entryPoint);
        if (plan.IsFailure)
            return Fail(locator.Fix(plan.Error));
        if (plan.Value.Count == 0)
            return Ok(source);

        // `-D` macros come before the text, so their names count as macro names too.
        string prefix = string.Concat((macros ?? []).Select(m => $"#define {m.Name} {m.Value ?? "1"}\n"));
        string lines = (prefix + source).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var macroNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in DefinedName().Matches(lines))
            macroNames.Add(m.Groups[1].Value);

        Result<string, Declaration> located = Locate(source, original.Text, plan.Value, locator, macroNames, preprocess);
        return located.IsSuccess
            ? Ok(located.Value)
            : Fail(locator.Fix(NotLocated(original.Text, file, entryPoint, located.Error)));
    }

    // ---- Locating the prototypes in the raw source, and proving the edit -------------------

    /// <summary>The most raw ranges tried for one prototype before refusing.</summary>
    private const int MaxLocateAttempts = 32;

    /// <summary>The most DXC preprocess runs one <see cref="Prepare"/> spends proving ranges.</summary>
    private const int MaxProofRuns = 64;

    /// <summary>
    /// The source with every prototype in <paramref name="plan"/> blanked, each range proven by
    /// DXC's preprocessor (see <see cref="Proves"/>); or the first prototype no candidate range
    /// removes alone. All positional ranges are tried together first (one run); otherwise each
    /// prototype in turn, with the ranges already proven blanked too, so the last proof covers the
    /// whole edit.
    /// </summary>
    private static Result<string, Declaration> Locate(
        string source, string preprocessed, IReadOnlyList<Declaration> plan, Locator locator,
        IReadOnlySet<string> macroNames, Func<string, PreprocessedText> preprocess)
    {
        List<Tok> significant = Significant(preprocessed);
        List<CandidateRanges> candidates = plan.Select(p => locator.Candidates(p, significant, macroNames)).ToList();
        int runs = 0;

        if (candidates.All(c => c.Positional))
        {
            string blanked = Blank(source, candidates.Select(c => c.Ranges[0]).ToList());
            runs++;
            if (Proves(blanked, Without(significant, plan), preprocess))
                return Result<string, Declaration>.Ok(blanked);
        }

        var chosen = new List<(int Start, int End)>();
        for (int i = 0; i < plan.Count; i++)
        {
            List<Tok> expected = Without(significant, plan.Take(i + 1).ToList());
            (int Start, int End)? found = null;
            // Two prototypes with the same tokens prove each other's range: one already taken is not this one's.
            foreach ((int Start, int End) range in candidates[i].Ranges
                         .Where(c => !chosen.Any(t => c.Start < t.End && t.Start < c.End))
                         .Take(MaxLocateAttempts))
            {
                if (runs++ >= MaxProofRuns)
                    break;
                if (Proves(Blank(source, [.. chosen, range]), expected, preprocess))
                {
                    found = range;
                    break;
                }
            }
            if (found is null)
                return Result<string, Declaration>.Fail(plan[i]);
            chosen.Add(found.Value);
        }
        return Result<string, Declaration>.Ok(Blank(source, chosen));
    }

    /// <summary>
    /// True when DXC's preprocessor turns <paramref name="candidate"/> into exactly the
    /// <paramref name="expected"/> token stream: the same kinds and spellings in the same order
    /// (directives it passes through, <c>#pragma</c>, included; its <c>#line</c> markers, which
    /// only say where a line came from, not).
    /// </summary>
    private static bool Proves(string candidate, List<Tok> expected, Func<string, PreprocessedText> preprocess)
    {
        PreprocessedText result = preprocess(candidate);
        if (!result.Succeeded)
            return false;
        List<Tok> actual = Significant(result.Text);
        if (actual.Count != expected.Count)
            return false;
        for (int i = 0; i < actual.Count; i++)
        {
            if (!SameToken(actual[i], expected[i]))
                return false;
        }
        return true;
    }

    /// <summary>The tokens of a preprocessed text that reach the compiler: everything but DXC's line markers.</summary>
    private static List<Tok> Significant(string preprocessed) =>
        Tokenize(preprocessed, rawText: false).Where(t => !IsLineMarker(t)).ToList();

    private static bool IsLineMarker(Tok t) => t.Kind == TokKind.Directive && LineMarker().IsMatch(t.Text);

    /// <summary>The raw ranges that could be a prototype, most likely first; <see cref="Positional"/> when the first is the positional one.</summary>
    private sealed record CandidateRanges(List<(int Start, int End)> Ranges, bool Positional);

    private static ShaderError NotLocated(string text, string file, string entryPoint, Declaration prototype) =>
        Refuse(text, file, prototype.NameLine, prototype.NameColumn,
            $"entry point '{entryPoint}' is declared more than once, and this declaration cannot be removed from the " +
            "source as written without changing anything else the compiler sees: a macro writes, renames or moves part " +
            "of it together with other code. " + CrashSentence + $" Write the prototype of '{entryPoint}' out on lines " +
            "of its own (macros inside it are fine), or remove it.");

    private static bool SameToken(Tok a, Tok b) => a.Kind == b.Kind && string.Equals(a.Text, b.Text, StringComparison.Ordinal);

    /// <summary>
    /// The preprocessed tokens with every listed declaration's tokens taken out. A directive DXC
    /// passes through (<c>#pragma</c>) inside a declaration is not one of its tokens: the edit keeps
    /// it (see <see cref="Blank"/>).
    /// </summary>
    private static List<Tok> Without(List<Tok> tokens, IReadOnlyList<Declaration> removed)
    {
        List<Declaration> spans = removed.OrderBy(d => d.Start).ToList();
        var kept = new List<Tok>(tokens.Count);
        int next = 0;
        foreach (Tok t in tokens)
        {
            while (next < spans.Count && spans[next].End <= t.Offset)
                next++;
            if (next >= spans.Count || t.Offset < spans[next].Start || t.Kind == TokKind.Directive)
                kept.Add(t);
        }
        return kept;
    }

    /// <summary>
    /// Where the lines of DXC's preprocessed text came from in the raw source. DXC marks them with
    /// <c>#line N "file"</c>; the raw source names the same places with its own <c>#line</c>
    /// directives (the flattener writes one per inlined <c>#include</c>), so a preprocessed line's
    /// (file, line) leads back to the raw physical line(s) that carry it. This only proposes where
    /// to look: every range it leads to is proven by DXC's preprocessor before it is used.
    /// </summary>
    private sealed class Locator
    {
        private readonly string _source;
        private readonly string _preprocessed;
        private readonly string _file;
        private readonly string _entryPoint;
        private readonly string _main;
        private readonly LineIndex _rawLines;
        private readonly LineIndex _outLines;
        private readonly (string File, int Line)[] _outPresumed;
        private readonly Dictionary<(string File, int Line), List<int>> _rawByPresumed = [];
        private readonly ILookup<int, Tok> _rawByLine;

        public Locator(string source, string preprocessed, string file, string entryPoint)
        {
            _source = source;
            _preprocessed = preprocessed;
            _file = file;
            _entryPoint = entryPoint;
            _rawLines = new LineIndex(source);
            _outLines = new LineIndex(preprocessed);

            // DXC's first marker names its main input; the raw source starts in it.
            Match first = LineMarker().Match(preprocessed.TrimStart());
            string main = first.Success && first.Groups[2].Success ? Unescape(first.Groups[2].Value) : string.Empty;
            _main = main;
            _outPresumed = Presumed(preprocessed, main);
            (string File, int Line)[] raw = Presumed(source, main);
            for (int i = 0; i < raw.Length; i++)
            {
                if (!_rawByPresumed.TryGetValue(raw[i], out List<int>? physical))
                    _rawByPresumed[raw[i]] = physical = [];
                physical.Add(i + 1);
            }
            _rawByLine = Tokenize(source, rawText: true)
                .Where(t => t.Kind is not (TokKind.Comment or TokKind.Directive))
                .ToLookup(t => _rawLines.Position(t.Offset).Line);
        }

        /// <summary>
        /// The candidate raw ranges for <paramref name="prototype"/> (read off the preprocessed
        /// text): each runs from the start of a raw token on a raw line the prototype's first token
        /// came from to the end of a raw token on a line its last token came from (exact token
        /// boundaries, never part of a token). The positional candidate (the prototype's first and
        /// last tokens at the same token positions in the raw lines as in the preprocessed ones, as
        /// when no macro touched them) comes first, then every plausible pair, narrowest first: a
        /// start that is the prototype's first token or a macro name, an end that is its last token,
        /// a macro name, or a macro call's ')'. Starts at or before the last raw spelling of the
        /// entry's name on that line come first: one after it (a macro name after the prototype)
        /// can begin it only if a macro writes the name.
        /// </summary>
        public CandidateRanges Candidates(Declaration prototype, List<Tok> significant, IReadOnlySet<string> macroNames)
        {
            var candidates = new List<(int Start, int End)>();
            int first = significant.FindIndex(t => t.Offset >= prototype.Start);
            int last = significant.FindLastIndex(t => t.Offset < prototype.End);
            if (first < 0 || last < first)
                return new CandidateRanges(candidates, false);

            int firstOutLine = _outLines.Position(significant[first].Offset).Line;
            int lastOutLine = _outLines.Position(significant[last].Offset).Line;
            int firstInLine = significant.Take(first).Count(t => _outLines.Position(t.Offset).Line == firstOutLine && t.Kind != TokKind.Directive);
            int lastInLine = significant.Take(last).Count(t => _outLines.Position(t.Offset).Line == lastOutLine && t.Kind != TokKind.Directive);

            bool positional = false;
            foreach (int startLine in RawLines(firstOutLine))
            {
                foreach (int endLine in RawLines(lastOutLine).Where(l => l >= startLine))
                {
                    List<Tok> onStartLine = _rawByLine[startLine].ToList();
                    List<Tok> onEndLine = _rawByLine[endLine].ToList();
                    if (firstInLine < onStartLine.Count && lastInLine < onEndLine.Count
                        && onEndLine[lastInLine].Offset >= onStartLine[firstInLine].Offset)
                    {
                        positional |= candidates.Count == 0;
                        candidates.Add((onStartLine[firstInLine].Offset, onEndLine[lastInLine].Offset + onEndLine[lastInLine].Text.Length));
                    }

                    int bound = onStartLine.LastOrDefault(t => t.IsWord(_entryPoint)) is { Text: not null } name ? name.Offset : int.MaxValue;
                    bool IsMacro(Tok t) => t.Kind == TokKind.Identifier && macroNames.Contains(t.Text);
                    foreach (Tok s in onStartLine.Where(t => SameToken(t, significant[first]) || IsMacro(t)).OrderBy(t => t.Offset <= bound ? 0 : 1).ThenByDescending(t => t.Offset))
                    {
                        foreach (Tok e in onEndLine.Where(t => t.Offset >= s.Offset && (SameToken(t, significant[last]) || t.Is(")") || IsMacro(t))))
                            candidates.Add((s.Offset, e.Offset + e.Text.Length));
                    }
                }
            }

            List<(int Start, int End)> usable = candidates.Where(c => c.End > c.Start).Distinct().ToList();
            return new CandidateRanges(usable, positional && usable.Count > 0 && usable[0] == candidates[0]);
        }

        /// <summary>The raw physical lines (1-based) a line of the preprocessed text came from, nearest first.</summary>
        private IEnumerable<int> RawLines(int outLine) =>
            outLine >= 1 && outLine <= _outPresumed.Length && _rawByPresumed.TryGetValue(_outPresumed[outLine - 1], out List<int>? lines)
                ? lines.Take(4)
                : [];

        /// <summary>
        /// <paramref name="error"/> with its column moved onto the raw source: the column of the
        /// entry's name on the raw line it came from (DXC's preprocessed text does not keep every
        /// column), when that line can be found.
        /// </summary>
        public ShaderError Fix(ShaderError error)
        {
            // A line DXC places in its own name for the main input (no #line of the source's own
            // before it) is the source file's.
            if (SameFile(error.File, _main))
                error = error with { File = _file };
            // The error's line is the author's; the raw lines that carry it are the candidates.
            foreach (KeyValuePair<(string File, int Line), List<int>> entry in _rawByPresumed)
            {
                string file = SameFile(entry.Key.File, _main) ? _file : entry.Key.File;
                if (entry.Key.Line != error.Line || !SameFile(file, error.File))
                    continue;
                foreach (int line in entry.Value)
                {
                    if (_rawByLine[line].FirstOrDefault(t => t.IsWord(_entryPoint)) is { Text: not null } name)
                        return error with { Column = _rawLines.Position(name.Offset).Column + 1 };
                }
            }
            return error;
        }

        private static bool SameFile(string a, string b) =>
            string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.Ordinal);

        /// <summary>The (file, line) each physical line of <paramref name="text"/> stands for, as its <c>#line</c> directives say.</summary>
        private static (string File, int Line)[] Presumed(string text, string startFile)
        {
            var index = new LineIndex(text);
            var presumed = new (string, int)[index.LineCount];
            string file = startFile;
            int line = 1;
            for (int i = 0; i < presumed.Length; i++)
            {
                presumed[i] = (file, line);
                Match marker = LineMarker().Match(index.Text(text, i + 1));
                if (marker.Success && int.TryParse(marker.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int next))
                {
                    line = next;
                    if (marker.Groups[2].Success)
                        file = Unescape(marker.Groups[2].Value);
                }
                else
                {
                    line++;
                }
            }
            return presumed;
        }

        private static string Unescape(string name) => name.Replace("\\\\", "\\", StringComparison.Ordinal).Replace('\\', '/');
    }

    // ---- What to blank ----------------------------------------------------------------------

    /// <summary>
    /// The prototypes to blank (empty when the source is to be compiled as is), or the refusal
    /// for a shape that cannot be compiled without the prototype.
    /// </summary>
    private static Result<IReadOnlyList<Declaration>, ShaderError> Plan(
        Analysis analysis, string text, string file, string entryPoint)
    {
        // No prototype: nothing to do. No definition or several: DXC's front end reports that
        // itself ("missing entry point definition", "ambiguous entry point function").
        if (analysis.Prototypes.Count == 0 || analysis.Definitions.Count != 1)
            return Result<IReadOnlyList<Declaration>, ShaderError>.Ok([]);

        Declaration definition = analysis.Definitions[0];
        var blank = new List<Declaration>();
        foreach (Declaration prototype in analysis.Prototypes)
        {
            bool sameReturn = string.Equals(prototype.ReturnKey, definition.ReturnKey, StringComparison.Ordinal);
            bool sameParameters = string.Equals(prototype.ParameterKey, definition.ParameterKey, StringComparison.Ordinal);
            if (sameParameters && !sameReturn)
            {
                // Two different built-in types are two types to DXC too: its front end reports
                // "functions that differ only in their return type" (fxc: X3068), measured for
                // float3/float4 and half4/float4. Any other spelling may be one type to DXC and
                // the back end's crash.
                if (BuiltinType().IsMatch(prototype.ReturnKey) && BuiltinType().IsMatch(definition.ReturnKey))
                    continue;
                return Result<IReadOnlyList<Declaration>, ShaderError>.Fail(Refuse(text, file, prototype.NameLine, prototype.NameColumn,
                    $"entry point '{entryPoint}' has a prototype whose return type ('{prototype.ReturnKey}') is spelled " +
                    $"differently from its definition's ('{definition.ReturnKey}'). {CrashSentence} Whether the two name one " +
                    $"type cannot be told safely here, so write the prototype's return type as the definition does, or " +
                    "remove the prototype."));
            }
            if (sameParameters && Lost(prototype, definition) is { } lost)
            {
                // DXC's front end merges some of what a prototype says into the definition (an
                // inherited attribute, a parameter's interpolation mode, a default argument), so
                // removing a prototype that says more than its definition could change the
                // compiled shader without a word. Only a prototype that adds nothing is removed.
                return Result<IReadOnlyList<Declaration>, ShaderError>.Fail(Refuse(text, file, prototype.NameLine, prototype.NameColumn,
                    $"entry point '{entryPoint}' has a prototype that carries {lost}, which its definition does not. " +
                    $"{CrashSentence} Removing this prototype could change the compiled shader, so write {lost} on the " +
                    $"definition of '{entryPoint}' as well, or remove the prototype."));
            }
            if (!sameParameters && analysis.Calls.Count > 0)
            {
                return Result<IReadOnlyList<Declaration>, ShaderError>.Fail(Refuse(text, file, prototype.NameLine, prototype.NameColumn,
                    $"entry point '{entryPoint}' has a prototype whose parameter list differs from its definition (an overload " +
                    $"with no definition of its own), and the source calls '{entryPoint}'. {CrashSentence} Which declaration a " +
                    "call resolves to cannot be told from the text, so this prototype cannot be removed safely. Remove the " +
                    "unused overload prototype."));
            }
            blank.Add(prototype);
        }

        if (blank.Count > 0)
        {
            int firstPrototype = blank.Min(p => p.Start);
            foreach (Token call in analysis.Calls)
            {
                if (call.Offset > firstPrototype && call.Offset < definition.Start)
                {
                    return Result<IReadOnlyList<Declaration>, ShaderError>.Fail(Refuse(text, file, call.Line, call.Column,
                        $"entry point '{entryPoint}' is called here, between its prototype and its definition. {CrashSentence} " +
                        "Without the prototype this call has no declaration to resolve to. Move the definition of " +
                        $"'{entryPoint}' above its first call (fxc accepts the original order; DXC's SPIR-V back end cannot " +
                        "compile it)."));
                }
            }
        }

        return Result<IReadOnlyList<Declaration>, ShaderError>.Ok(blank);
    }

    /// <summary>
    /// What a prototype with the definition's signature says that the definition does not (an
    /// attribute, a leading modifier, a parameter's interpolation modifier or default value), or
    /// null when it says nothing more. Semantics are not compared: DXC takes an entry point's
    /// semantics from its definition only (measured on DirectX 12: a semantic written only on the
    /// prototype gives "Semantic must be defined for all parameters of an entry function"), so a
    /// prototype's semantics change nothing and DXC reports a missing one itself.
    /// </summary>
    /// <remarks>
    /// A leading modifier counts even when it looks inert: on DirectX 12 the pinned DXC answers an
    /// <c>inline</c> or <c>static</c> prototype of the entry with "cannot find entry function", so
    /// removing that prototype would make OpenGL compile what DXC itself refuses.
    /// </remarks>
    private static string? Lost(Declaration prototype, Declaration definition)
    {
        if (prototype.Attributes.FirstOrDefault(a => !definition.Attributes.Contains(a)) is { } attribute)
            return $"the attribute '{attribute}'";
        if (prototype.Modifiers.FirstOrDefault(m => !definition.Modifiers.Contains(m)) is { } modifier)
            return $"the modifier '{modifier}'";
        for (int i = 0; i < prototype.Parameters.Count && i < definition.Parameters.Count; i++)
        {
            Parameter p = prototype.Parameters[i];
            Parameter d = definition.Parameters[i];
            if (p.HasDefault)
                return $"a default value for parameter {i + 1}";
            if (p.Qualifiers.FirstOrDefault(q => !d.Qualifiers.Contains(q)) is { } qualifier)
                return $"the modifier '{qualifier}' on parameter {i + 1}";
        }
        return null;
    }

    private const string CrashSentence =
        "DXC's SPIR-V back end (the OpenGL and Vulkan targets, and the raylib and SkSL converters) crashes the process " +
        "on an entry point that is declared more than once, so ShadowDusk removes the entry point's prototypes before " +
        "compiling.";

    private static ShaderError Unread(string text, string file, string entryPoint, Token at, string? why = null) =>
        Refuse(text, file, at.Line, at.Column,
            $"entry point '{entryPoint}' is named here at global scope in a form ShadowDusk cannot read as a declaration, " +
            $"beside another declaration of it{(why is null ? "" : $" (and the source cannot be preprocessed here: {why})")}. {CrashSentence} Whether this is a second declaration cannot be told safely, " +
            $"so write the declarations of '{entryPoint}' in the plain form (return type, name, parameters, semantic), or " +
            "remove its prototype.");

    private static ShaderError Refuse(string text, string file, int line, int column, string message)
    {
        (string mappedFile, int mappedLine) = new SourceLineMap(text, file).Resolve(line);
        return new ShaderError(File: mappedFile, Line: mappedLine, Column: column, Code: ErrorCode, Message: message);
    }

    private static Result<string, ShaderError> Ok(string text) => Result<string, ShaderError>.Ok(text);

    private static Result<string, ShaderError> Fail(ShaderError error) => Result<string, ShaderError>.Fail(error);

    /// <summary>
    /// A cheap, conservative gate that skips DXC's preprocess run only where the entry's name
    /// cannot reach DXC twice: the source never spells it, or spells it once and defines no macro
    /// at all (no <c>define</c> word anywhere, comments and literals included), so that one
    /// spelling is one token. Two declarations need the name written twice, or a macro that
    /// writes it (an object-like macro used twice, a function-like macro whose argument it is),
    /// or a token paste. Directive syntax is never read here: a comment, a form feed or a vertical
    /// tab between <c>#</c>, <c>define</c> and the macro name (the review of 2026-10-29) is
    /// whitespace to DXC, so any <c>define</c> word sends the source to DXC's preprocessor. So
    /// does a <c>-D</c> macro that is function-like or spells the name, a token paste, a line
    /// splice (a backslash before a line break, which can join a name DXC then reads), a trigraph
    /// and a <c>%:</c> digraph (a directive).
    /// </summary>
    private static bool CannotBeDeclaredTwice(string source, string name, IReadOnlyList<(string Name, string? Value)> macros)
    {
        if (source.Contains("##", StringComparison.Ordinal) || LineSplice().IsMatch(source)
            || source.Contains("??", StringComparison.Ordinal) || source.Contains("%:", StringComparison.Ordinal))
        {
            return false;
        }
        foreach ((string macro, string? value) in macros)
        {
            if (macro.Contains('(', StringComparison.Ordinal)
                || IndexOfWord(macro, name, 0) >= 0
                || (value is not null && IndexOfWord(value, name, 0) >= 0))
            {
                return false;
            }
        }
        int first = IndexOfWord(source, name, 0);
        if (first < 0)
            return true;
        return IndexOfWord(source, name, first + name.Length) < 0 && IndexOfWord(source, "define", 0) < 0;
    }

    private static int IndexOfWord(string source, string word, int from)
    {
        for (int i = source.IndexOf(word, from, StringComparison.Ordinal); i >= 0; i = source.IndexOf(word, i + 1, StringComparison.Ordinal))
        {
            bool before = i > 0 && IsIdentifierChar(source[i - 1]);
            bool after = i + word.Length < source.Length && IsIdentifierChar(source[i + word.Length]);
            if (!before && !after)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Replaces every character of each range with spaces, keeping line breaks: as many spaces as
    /// the character's UTF-8 bytes, because DXC counts columns in bytes of its UTF-8 input, so a
    /// later diagnostic on the same line keeps its column. A preprocessor directive on a line of
    /// its own inside a range (a <c>#line</c>, <c>#if</c>, <c>#define</c> or <c>#pragma</c> in a
    /// multi-line prototype) is kept whole: only the prototype's own text is blanked, so a
    /// <c>#line</c> still renumbers what follows and a <c>#define</c> still defines (the proof
    /// then checks the result like any other).
    /// </summary>
    private static string Blank(string source, IReadOnlyList<(int Start, int End)> spans)
    {
        var blanked = new bool[source.Length];
        foreach ((int start, int end) in spans)
            Array.Fill(blanked, true, start, end - start);
        if (spans.Any(s => source.AsSpan(s.Start, s.End - s.Start).IndexOfAny('\n', '\r') >= 0))
        {
            foreach (Tok directive in Tokenize(source, rawText: true).Where(t => t.Kind == TokKind.Directive))
            {
                if (spans.Any(s => directive.Offset > s.Start && directive.Offset < s.End))
                    Array.Fill(blanked, false, directive.Offset, directive.Text.Length);
            }
        }

        var builder = new StringBuilder(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (!blanked[i] || c is '\r' or '\n')
                builder.Append(c);
            else if (char.IsHighSurrogate(c) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]) && blanked[i + 1])
                builder.Append(' ', Encoding.UTF8.GetByteCount(source.AsSpan(i++, 2)));
            else
                builder.Append(' ', c < 0x80 ? 1 : Encoding.UTF8.GetByteCount(source.AsSpan(i, 1)));
        }
        return builder.ToString();
    }

    // ---- Reading declarations ---------------------------------------------------------------

    /// <summary>A global-scope function declaration of the entry name.</summary>
    /// <param name="Start">Offset of its first token (attributes, storage modifiers, return type).</param>
    /// <param name="End">Offset one past its last character (a prototype's <c>;</c>, a definition's <c>{</c>).</param>
    /// <param name="NameLine">Physical line (1-based) of the function name.</param>
    /// <param name="NameColumn">Column (1-based) of the function name.</param>
    /// <param name="ReturnKey">The normalized return type.</param>
    /// <param name="ParameterKey">The normalized parameter types.</param>
    /// <param name="Attributes">Each leading <c>[...]</c> attribute, spelled out.</param>
    /// <param name="Modifiers">The leading modifiers that are not part of the return type (<c>static</c>, <c>precise</c>, ...).</param>
    /// <param name="Parameters">What each parameter says beyond its type.</param>
    private sealed record Declaration(
        int Start, int End, int NameLine, int NameColumn, string ReturnKey, string ParameterKey,
        IReadOnlyList<string> Attributes, IReadOnlyList<string> Modifiers, IReadOnlyList<Parameter> Parameters);

    /// <summary>A parameter's interpolation (and <c>precise</c>) modifiers, and whether it has a default value.</summary>
    private sealed record Parameter(IReadOnlyList<string> Qualifiers, bool HasDefault);

    /// <summary>A position in the analyzed text.</summary>
    private sealed record Token(int Line, int Column, int Offset = 0);

    /// <param name="Prototypes">Global-scope declarations of the entry without a body.</param>
    /// <param name="Definitions">Global-scope declarations of the entry with a body.</param>
    /// <param name="Calls">Uses of the entry name inside a body (calls), technique references excluded.</param>
    /// <param name="Unread">Global-scope uses of the name that may be declarations but do not read as one.</param>
    private sealed record Analysis(
        IReadOnlyList<Declaration> Prototypes,
        IReadOnlyList<Declaration> Definitions,
        IReadOnlyList<Token> Calls,
        IReadOnlyList<Token> Unread)
    {
        public int DeclarationShaped => Prototypes.Count + Definitions.Count + Unread.Count;
    }

    /// <summary>The declarations, calls and unread uses of <paramref name="name"/> in DXC's preprocessed text.</summary>
    private static Analysis Analyze(string text, string name)
    {
        // DXC's line markers say where a line came from and are no code: a declaration may run across one.
        List<Tok> tokens = Tokenize(text, rawText: false).Where(t => t.Kind != TokKind.Comment && !IsLineMarker(t)).ToList();
        var lines = new LineIndex(text);
        Dictionary<string, string> typedefs = Typedefs(tokens);

        var prototypes = new List<Declaration>();
        var definitions = new List<Declaration>();
        var calls = new List<Token>();
        var unread = new List<Token>();

        int depth = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            Tok t = tokens[i];
            if (t.Kind == TokKind.Directive)
                continue;
            if (t.Is("{"))
            {
                depth++;
                continue;
            }
            if (t.Is("}"))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }
            if (t.Kind != TokKind.Identifier || !string.Equals(t.Text, name, StringComparison.Ordinal))
                continue;

            (int line, int column) = lines.Position(t.Offset);
            var at = new Token(line, column + 1, t.Offset);
            // A directive the view passes through (`#pragma`, `#line`) may sit between the name and
            // its '(': still call-shaped, and then not a declaration this reading accepts.
            int next = i + 1;
            while (next < tokens.Count && tokens[next].Kind == TokKind.Directive)
                next++;
            bool calledShape = next < tokens.Count && tokens[next].Is("(");
            if (!calledShape)
            {
                // A parenthesised name, `float4 (PS)(...)`, is a declarator to DXC (a declaration
                // this reading does not model) or, in an expression, a call.
                if (ParenthesisedCallee(tokens, i) is int open)
                {
                    if (depth > 0 || !IsTypeLike(open > 0 ? tokens[open - 1] : null))
                        calls.Add(at);
                    else
                        unread.Add(at);
                    continue;
                }

                // Otherwise, every macro is expanded in DXC's preprocessed text, so a use without
                // '(' declares nothing.
                continue;
            }
            if (IsTechniqueReference(tokens, i))
                continue;
            if (depth > 0)
            {
                calls.Add(at);
                continue;
            }

            if (TryReadDeclaration(text, tokens, typedefs, i, out Declaration? declaration, out int last))
            {
                (tokens[last].Is("{") ? definitions : prototypes).Add(declaration! with { NameLine = line, NameColumn = column + 1 });
                i = tokens[last].Is("{") ? last - 1 : last; // a body's braces are walked by this loop
                continue;
            }

            // Not a declaration: an expression in a global initializer (`= PS(`, `max(PS(`) is a
            // call; a type-like token before the name, or a ',' outside any parentheses (a second
            // declarator, `float4 A(), PS(...);`), is a declaration this reading failed on.
            Tok? previous = i > 0 ? tokens[i - 1] : null;
            if (IsTypeLike(previous) || (previous is { } comma && comma.Is(",") && StatementParenDepth(tokens, i) == 0))
                unread.Add(at);
            else
                calls.Add(at);
        }

        return new Analysis(prototypes, definitions, calls, unread);
    }

    /// <summary>A token that may end a type (so a name after it may be declared): a word, a template's <c>&gt;</c>, an attribute's <c>]</c>.</summary>
    private static bool IsTypeLike(Tok? token) =>
        token is { Kind: TokKind.Identifier } || (token is { Kind: TokKind.Punctuation } p && p.Text is ">" or "]");

    /// <summary>
    /// For a name wrapped in parentheses and followed by a parameter list, <c>(PS)(</c> or
    /// <c>((PS))(</c>, the index of the outermost <c>(</c>; null for any other use.
    /// </summary>
    private static int? ParenthesisedCallee(List<Tok> tokens, int nameIndex)
    {
        int open = nameIndex;
        int close = nameIndex;
        while (open > 0 && tokens[open - 1].Is("(") && close + 1 < tokens.Count && tokens[close + 1].Is(")"))
        {
            open--;
            close++;
        }
        return open < nameIndex && close + 1 < tokens.Count && tokens[close + 1].Is("(") ? open : null;
    }

    /// <summary>The parenthesis depth at <paramref name="index"/> within its global statement (since the last <c>;</c>, <c>{</c> or <c>}</c>).</summary>
    private static int StatementParenDepth(List<Tok> tokens, int index)
    {
        int depth = 0;
        for (int j = index - 1; j >= 0 && !(tokens[j].Text is ";" or "{" or "}" && tokens[j].Kind == TokKind.Punctuation); j--)
        {
            if (tokens[j].Is(")"))
                depth--;
            else if (tokens[j].Is("("))
                depth++;
        }
        return depth;
    }

    /// <summary><c>compile ps_3_0 PS(...)</c>, or <c>CompileShader(ps_4_0, PS(...))</c>.</summary>
    private static bool IsTechniqueReference(List<Tok> tokens, int nameIndex) =>
        (nameIndex >= 2 && tokens[nameIndex - 1].Kind == TokKind.Identifier && tokens[nameIndex - 2].IsWord("compile"))
        || (nameIndex >= 4 && tokens[nameIndex - 1].Is(",") && tokens[nameIndex - 2].Kind == TokKind.Identifier
            && tokens[nameIndex - 3].Is("(") && tokens[nameIndex - 4].IsWord("CompileShader"));

    /// <summary>
    /// Reads a global-scope function declaration whose name token is at <paramref name="nameIndex"/>:
    /// storage modifiers and attributes, a return type, the name, a balanced parameter list,
    /// then an optional semantic, ending at a <c>;</c> (a prototype) or a <c>{</c> (a definition),
    /// with a declaration boundary (<c>;</c>, <c>}</c>, a directive, the start) before it.
    /// </summary>
    private static bool TryReadDeclaration(
        string text, List<Tok> tokens, Dictionary<string, string> typedefs, int nameIndex,
        out Declaration? declaration, out int last)
    {
        declaration = null;
        last = -1;

        // The return type: `float4`, `S`, or a template `vector<float, 4>`.
        int first = nameIndex - 1;
        if (first < 0)
            return false;
        if (tokens[first].Is(">"))
        {
            int angles = 0;
            for (; first >= 0; first--)
            {
                if (tokens[first].Is(">"))
                    angles++;
                else if (tokens[first].Is("<") && --angles == 0)
                    break;
                else if (tokens[first].Text is ";" or "{" or "}" or "(" or ")" || tokens[first].Kind == TokKind.Directive)
                    return false;
            }
            first--;
        }
        if (first < 0 || tokens[first].Kind != TokKind.Identifier || Keyword().IsMatch(tokens[first].Text))
            return false;

        // Modifiers and attributes before it.
        while (first > 0)
        {
            Tok previous = tokens[first - 1];
            if (previous.Kind == TokKind.Identifier && DeclarationModifier().IsMatch(previous.Text))
            {
                first--;
            }
            else if (previous.Is("]"))
            {
                int brackets = 0;
                int j = first - 1;
                for (; j >= 0; j--)
                {
                    if (tokens[j].Is("]"))
                        brackets++;
                    else if (tokens[j].Is("[") && --brackets == 0)
                        break;
                    else if (tokens[j].Text is ";" or "{" or "}" || tokens[j].Kind == TokKind.Directive)
                        return false;
                }
                if (j < 0)
                    return false;
                first = j;
            }
            else
            {
                break;
            }
        }
        if (first > 0 && !(tokens[first - 1].Text is ";" or "}" || tokens[first - 1].Kind == TokKind.Directive))
            return false;

        // The balanced parameter list.
        int parens = 0;
        int close = -1;
        for (int j = nameIndex + 1; j < tokens.Count; j++)
        {
            Tok t = tokens[j];
            if (t.Kind == TokKind.Directive || t.Text is "{" or "}" or ";")
                return false;
            if (t.Is("("))
                parens++;
            else if (t.Is(")") && --parens == 0)
            {
                close = j;
                break;
            }
        }
        if (close < 0)
            return false;

        // Semantics (`: COLOR0`, `: register(c0)`), then the terminator. Anything else (a second
        // declarator after a ',', an initializer) is not a shape this reading models.
        int k = close + 1;
        while (k + 1 < tokens.Count && tokens[k].Is(":") && tokens[k + 1].Kind == TokKind.Identifier)
        {
            k += 2;
            if (k < tokens.Count && tokens[k].Is("("))
            {
                int depth = 0;
                for (; k < tokens.Count; k++)
                {
                    if (tokens[k].Kind == TokKind.Directive || tokens[k].Text is "{" or "}" or ";")
                        return false;
                    if (tokens[k].Is("("))
                        depth++;
                    else if (tokens[k].Is(")") && --depth == 0)
                        break;
                }
                k++;
            }
        }
        if (k >= tokens.Count || !(tokens[k].Is(";") || tokens[k].Is("{")))
            return false;
        last = k;

        List<Tok> leading = tokens.GetRange(first, nameIndex - first);
        List<Tok> parameters = tokens.GetRange(nameIndex + 2, close - nameIndex - 2);
        declaration = new Declaration(
            Start: tokens[first].Offset,
            End: tokens[k].Offset + 1,
            NameLine: 0,
            NameColumn: 0,
            ReturnKey: ReturnKey(leading, typedefs),
            ParameterKey: ParameterKey(parameters, typedefs),
            Attributes: Attributes(leading),
            Modifiers: LeadingModifiers(leading),
            Parameters: ParameterDetails(parameters));
        return true;
    }

    /// <summary>Each top-level <c>[...]</c> group among a declaration's leading tokens, spelled out.</summary>
    private static List<string> Attributes(List<Tok> leading)
    {
        var attributes = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < leading.Count; i++)
        {
            if (leading[i].Is("[") && depth++ == 0)
                start = i;
            else if (leading[i].Is("]") && --depth == 0)
                attributes.Add(Spell(leading.GetRange(start, i - start + 1)));
        }
        return attributes;
    }

    /// <summary>The storage-class modifiers among a declaration's leading tokens (outside attributes).</summary>
    private static List<string> LeadingModifiers(List<Tok> leading)
    {
        var modifiers = new List<string>();
        int depth = 0;
        foreach (Tok t in leading)
        {
            if (t.Is("["))
                depth++;
            else if (t.Is("]"))
                depth--;
            else if (depth == 0 && t.Kind == TokKind.Identifier && StorageModifier().IsMatch(t.Text))
                modifiers.Add(t.Text);
        }
        return modifiers;
    }

    /// <summary>Per parameter: its interpolation and <c>precise</c> modifiers, and whether it has a default value.</summary>
    private static List<Parameter> ParameterDetails(List<Tok> tokens)
    {
        var parameters = new List<Parameter>();
        if (tokens.Count == 0 || (tokens.Count == 1 && tokens[0].IsWord("void")))
            return parameters;
        foreach (List<Tok> parameter in SplitTopLevel(tokens))
        {
            List<string> qualifiers = parameter
                .TakeWhile(t => t.Kind == TokKind.Identifier && ParameterModifier().IsMatch(t.Text))
                .Select(t => t.Text)
                .Where(m => InterpolationModifier().IsMatch(m))
                .ToList();
            parameters.Add(new Parameter(qualifiers, HasDefault: parameter.Any(t => t.Is("="))));
        }
        return parameters;
    }

    /// <summary>The return type, without attributes or storage modifiers, with aliases resolved.</summary>
    private static string ReturnKey(List<Tok> tokens, Dictionary<string, string> typedefs)
    {
        var kept = new List<Tok>();
        int brackets = 0;
        foreach (Tok t in tokens)
        {
            if (t.Is("["))
                brackets++;
            else if (t.Is("]"))
                brackets--;
            else if (brackets == 0 && !(t.Kind == TokKind.Identifier && StorageModifier().IsMatch(t.Text)))
                kept.Add(t);
        }
        return TypeKey(kept, typedefs);
    }

    /// <summary>
    /// The parameter types in order, without names, semantics, default values, the modifiers that
    /// are not part of a function's signature (<c>in</c>, <c>const</c>, interpolation) or spacing
    /// differences; a lone <c>void</c> is no parameters.
    /// </summary>
    private static string ParameterKey(List<Tok> tokens, Dictionary<string, string> typedefs)
    {
        if (tokens.Count == 0 || (tokens.Count == 1 && tokens[0].IsWord("void")))
            return string.Empty;

        var keys = new List<string>();
        foreach (List<Tok> parameter in SplitTopLevel(tokens))
        {
            List<Tok> p = parameter.TakeWhile(t => !(t.Is(":") || t.Is("="))).ToList();

            // Array brackets stay with the type (`float a[4]` and `float b[4]` are one parameter type).
            string brackets = string.Empty;
            int arrayStart = p.FindIndex(t => t.Is("["));
            if (arrayStart >= 0)
            {
                brackets = string.Concat(p.Skip(arrayStart).Select(t => t.Text));
                p = p.Take(arrayStart).ToList();
            }

            List<Tok> modifiers = p.TakeWhile(t => t.Kind == TokKind.Identifier && ParameterModifier().IsMatch(t.Text)).ToList();
            p = p.Skip(modifiers.Count).ToList();
            if (p.Count >= 2 && p[^1].Kind == TokKind.Identifier)
                p.RemoveAt(p.Count - 1); // the parameter's name

            string kept = string.Join(' ', modifiers.Select(m => m.Text).Where(m => !IgnoredParameterModifier().IsMatch(m)));
            string type = TypeKey(p, typedefs);
            keys.Add((kept.Length > 0 ? kept + " " : string.Empty) + type + brackets);
        }
        return string.Join(",", keys);
    }

    private static IEnumerable<List<Tok>> SplitTopLevel(List<Tok> tokens)
    {
        int depth = 0;
        var current = new List<Tok>();
        foreach (Tok t in tokens)
        {
            if (t.Text is "(" or "<" or "[")
                depth++;
            else if (t.Text is ")" or ">" or "]")
                depth = Math.Max(0, depth - 1);
            else if (t.Is(",") && depth == 0)
            {
                yield return current;
                current = [];
                continue;
            }
            current.Add(t);
        }
        yield return current;
    }

    /// <summary>
    /// A type written as tokens, in one spelling: typedefs resolved, <c>vector&lt;T, N&gt;</c> as
    /// <c>TN</c>, <c>matrix&lt;T, R, C&gt;</c> as <c>TRxC</c>, and DXC's scalar aliases as their
    /// base type.
    /// </summary>
    private static string TypeKey(List<Tok> tokens, Dictionary<string, string> typedefs)
    {
        string key = Spell(tokens);
        for (int guard = 0; guard < 16 && typedefs.TryGetValue(key, out string? aliased); guard++)
            key = aliased;
        return CanonicalType(key);
    }

    private static string Spell(IEnumerable<Tok> tokens)
    {
        var builder = new StringBuilder();
        Tok? previous = null;
        foreach (Tok t in tokens)
        {
            if (previous is { Kind: TokKind.Identifier or TokKind.Number } && t.Kind is TokKind.Identifier or TokKind.Number)
                builder.Append(' ');
            builder.Append(t.Text);
            previous = t;
        }
        return builder.ToString();
    }

    private static string CanonicalType(string key)
    {
        Match vector = VectorTemplate().Match(key);
        if (vector.Success)
            key = vector.Groups[1].Value + vector.Groups[2].Value;
        else if (string.Equals(key, "vector", StringComparison.Ordinal))
            key = "float4";
        Match matrix = MatrixTemplate().Match(key);
        if (matrix.Success)
            key = matrix.Groups[1].Value + matrix.Groups[2].Value + "x" + matrix.Groups[3].Value;
        else if (string.Equals(key, "matrix", StringComparison.Ordinal))
            key = "float4x4";

        Match alias = ScalarAlias().Match(key);
        if (alias.Success)
        {
            string scalar = alias.Groups[1].Value switch
            {
                "dword" or "uint32_t" => "uint",
                "int32_t" => "int",
                "float32_t" => "float",
                _ => "double",
            };
            key = scalar + alias.Groups[2].Value;
        }
        return key;
    }

    /// <summary>File-scope <c>typedef &lt;type&gt; &lt;name&gt;;</c>, the alias spelled as its canonical type.</summary>
    private static Dictionary<string, string> Typedefs(List<Tok> tokens)
    {
        var typedefs = new Dictionary<string, string>(StringComparer.Ordinal);
        int depth = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Is("{"))
                depth++;
            else if (tokens[i].Is("}"))
                depth = Math.Max(0, depth - 1);
            if (depth != 0 || !tokens[i].IsWord("typedef"))
                continue;

            int end = tokens.FindIndex(i, t => t.Text is ";" or "{" or "}" || t.Kind == TokKind.Directive);
            if (end < 0 || !tokens[end].Is(";") || end - i < 3 || tokens[end - 1].Kind != TokKind.Identifier)
                continue;
            List<Tok> type = tokens.GetRange(i + 1, end - i - 2);
            if (type.Any(t => t.Text is "[" or "]" or "(" or ")"))
                continue;
            typedefs[tokens[end - 1].Text] = TypeKey(type.Where(t => !t.IsWord("const")).ToList(), typedefs);
        }
        return typedefs;
    }

    // ---- A small tokenizer: positions exact, nothing dropped -------------------------------

    private enum TokKind
    {
        Identifier,
        Number,
        String,
        Punctuation,
        Comment,
        Directive,
    }

    private readonly record struct Tok(TokKind Kind, string Text, int Offset)
    {
        public bool Is(string punctuation) => Kind == TokKind.Punctuation && string.Equals(Text, punctuation, StringComparison.Ordinal);

        public bool IsWord(string word) => Kind == TokKind.Identifier && string.Equals(Text, word, StringComparison.Ordinal);
    }

    /// <summary>
    /// Identifiers, numbers, string literals, single-character punctuation, comments, and whole
    /// preprocessor directive lines (a <c>#</c> first on its line, with backslash continuations).
    /// </summary>
    private static List<Tok> Tokenize(string text, bool rawText)
    {
        var tokens = new List<Tok>();
        bool lineStart = true;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (IsLineBreak(text, i))
            {
                lineStart = true;
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            int start = i;
            if (c == '#' && lineStart)
            {
                while (i < text.Length && text[i] is not ('\n' or '\r'))
                {
                    // A backslash-newline continues the directive (any of \n, \r\n, \r).
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '\n' or '\r')
                        i += text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n' ? 3 : 2;
                    else
                        i++;
                }
                tokens.Add(new Tok(TokKind.Directive, text[start..i], start));
                continue;
            }

            lineStart = false;
            if (rawText && c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] is not ('\n' or '\r'))
                    i++;
                tokens.Add(new Tok(TokKind.Comment, text[start..i], start));
            }
            else if (rawText && c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
                tokens.Add(new Tok(TokKind.Comment, text[start..i], start));
            }
            else if (IsIdentifierChar(c) && !char.IsAsciiDigit(c))
            {
                while (i < text.Length && IsIdentifierChar(text[i]))
                    i++;
                tokens.Add(new Tok(TokKind.Identifier, text[start..i], start));
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                while (i < text.Length && (IsIdentifierChar(text[i]) || text[i] == '.'
                       || ((text[i] is '+' or '-') && text[i - 1] is 'e' or 'E' && !text[start..i].StartsWith("0x", StringComparison.OrdinalIgnoreCase))))
                {
                    i++;
                }
                tokens.Add(new Tok(TokKind.Number, text[start..i], start));
            }
            else if (c == '"')
            {
                i++;
                while (i < text.Length && text[i] is not ('"' or '\n' or '\r'))
                    i += text[i] == '\\' ? 2 : 1;
                i = Math.Min(text.Length, i + 1);
                tokens.Add(new Tok(TokKind.String, text[start..i], start));
            }
            else
            {
                i++;
                tokens.Add(new Tok(TokKind.Punctuation, text[start..i], start));
            }
        }
        return tokens;
    }

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>
    /// True at the character that ends a line: a '\n', or a '\r' not followed by one (a lone
    /// carriage return is a line break to DXC and to the managed preprocessor, which turns it into
    /// '\n'); the '\r' of a "\r\n" is part of the line's end, not a break of its own.
    /// </summary>
    private static bool IsLineBreak(string text, int i) =>
        text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'));

    /// <summary>
    /// Line starts of a text, for (line, column) positions, with line breaks counted as the
    /// managed preprocessor counts them (see <see cref="IsLineBreak"/>).
    /// </summary>
    private sealed class LineIndex
    {
        private readonly List<int> _starts = [0];

        public LineIndex(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (IsLineBreak(text, i))
                    _starts.Add(i + 1);
            }
        }

        public int LineCount => _starts.Count;

        /// <summary>The text of the 1-based physical <paramref name="line"/> of <paramref name="text"/>, without its line break.</summary>
        public string Text(string text, int line)
        {
            int start = _starts[line - 1];
            int end = line < _starts.Count ? _starts[line] : text.Length;
            while (end > start && text[end - 1] is '\n' or '\r')
                end--;
            return text[start..end];
        }

        /// <summary>The 1-based line and 0-based column of <paramref name="offset"/>.</summary>
        public (int Line, int Column) Position(int offset)
        {
            int index = _starts.BinarySearch(offset);
            int line = index >= 0 ? index : ~index - 1;
            return (line + 1, offset - _starts[line]);
        }
    }

    /// <summary>
    /// A <c>#line N "file"</c> directive (DXC's line marker, or the source's own), or the GNU line
    /// marker <c># N "file"</c> clang reads the same way: the line number, then the file if named.
    /// </summary>
    [GeneratedRegex(@"^\s*#\s*(?:line\s+)?(\d+)(?:\s+""((?:[^""\\]|\\.)*)"")?", RegexOptions.CultureInvariant)]
    private static partial Regex LineMarker();

    /// <summary>A backslash before a line break, spaces between allowed (clang splices it with a warning).</summary>
    [GeneratedRegex(@"\\[ \t\f\v]*\n", RegexOptions.CultureInvariant)]
    private static partial Regex LineSplice();

    /// <summary>Words that end a declaration's leading modifiers (they are part of it, or before it).</summary>
    [GeneratedRegex(@"^(?:static|inline|noinline|precise|export|extern|const|unsigned|row_major|column_major|snorm|unorm|uniform)$", RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationModifier();

    /// <summary>Modifiers that are not part of a function's type.</summary>
    [GeneratedRegex(@"^(?:static|inline|noinline|precise|export|extern)$", RegexOptions.CultureInvariant)]
    private static partial Regex StorageModifier();

    [GeneratedRegex(@"^(?:in|out|inout|const|uniform|linear|centroid|nointerpolation|noperspective|sample|precise|point|line|lineadj|triangle|triangleadj)$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterModifier();

    /// <summary>Parameter modifiers that do not change the function's signature.</summary>
    [GeneratedRegex(@"^(?:in|const|linear|centroid|nointerpolation|noperspective|sample|precise)$", RegexOptions.CultureInvariant)]
    private static partial Regex IgnoredParameterModifier();

    /// <summary>
    /// Parameter modifiers outside the signature that still change the compiled shader (how an
    /// input is interpolated, or its precision), so a prototype's must be on the definition too.
    /// </summary>
    [GeneratedRegex(@"^(?:linear|centroid|nointerpolation|noperspective|sample|precise)$", RegexOptions.CultureInvariant)]
    private static partial Regex InterpolationModifier();


    /// <summary>The name of each <c>#define</c> (object-like or function-like).</summary>
    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex DefinedName();

    /// <summary>Words that cannot be a return type (the name follows a statement keyword, not a type).</summary>
    [GeneratedRegex(@"^(?:return|compile|CompileShader|else|case|do|typedef|technique|technique10|technique11|pass)$", RegexOptions.CultureInvariant)]
    private static partial Regex Keyword();

    /// <summary>A built-in scalar, vector or matrix type (or void), after alias resolution.</summary>
    [GeneratedRegex(@"^(?:void|(?:bool|int|uint|half|float|double|min16float|min10float|min16int|min12int|min16uint)(?:[1-4](?:x[1-4])?)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex BuiltinType();

    [GeneratedRegex(@"^vector<(\w+),([1-4])>$", RegexOptions.CultureInvariant)]
    private static partial Regex VectorTemplate();

    [GeneratedRegex(@"^matrix<(\w+),([1-4]),([1-4])>$", RegexOptions.CultureInvariant)]
    private static partial Regex MatrixTemplate();

    [GeneratedRegex(@"^(dword|int32_t|uint32_t|float32_t|float64_t)((?:[1-4](?:x[1-4])?)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScalarAlias();
}
