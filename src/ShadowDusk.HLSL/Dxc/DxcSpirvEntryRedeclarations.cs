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
/// converters. (The browser's DXC build does not fault on it; it fails the compile with
/// "generated SPIR-V is invalid: 2 Entry points cannot share the same name". The same
/// preparation runs there, so both hosts compile the same text.)</para>
///
/// <para><b>Why removing the prototype is faithful.</b> DXC's own DXIL path takes the entry's
/// semantics from the definition and never from a prototype, and fxc (<c>mgfxc</c> 3.8.4.1)
/// compiles these shapes. So a prototype whose return type and parameter types match the single
/// definition, and that says nothing the definition does not, is blanked from the compile's
/// input: replaced by spaces, keeping every line break, so no diagnostic line or column moves.
/// DXC then sees one declaration and compiles the definition it would have compiled. A
/// prototype that says MORE than its definition (an attribute, a leading modifier such as
/// <c>precise</c>, <c>inline</c> or <c>static</c>, a parameter's interpolation modifier or default
/// value) is refused instead, because DXC's front end may merge that into the definition (and
/// answers an <c>inline</c>/<c>static</c> entry with "cannot find entry function"), so removing
/// it could change the shader without a word. An overload prototype (different parameters, no
/// definition of its own) of an entry function nothing calls is inert, fxc compiles past it, and
/// it is blanked too.</para>
///
/// <para><b>Decided on the preprocessed text, removed from the raw text by proof.</b> DXC
/// compiles the text after its preprocessor, so every decision is made on the managed
/// preprocessed view (<see cref="FxMacroPreprocessor.ProcessForCompiler(string, string, IReadOnlyDictionary{int, bool})"/>:
/// conditionals evaluated, macros expanded, comments replaced by spaces, one output line per input
/// line): which declarations of the entry exist, which are prototypes, and which tokens each is.
/// The raw range to blank runs between exact raw token boundaries on the prototype's first and
/// last lines, and is accepted only when the source with that range blanked preprocesses to
/// exactly the original view's tokens minus the prototype's (see <see cref="Search"/>). So a
/// macro call before the prototype on its line (<c>DECL_PARAM(Unused) float4 PS(...);</c>)
/// stays, a prototype a macro completes (<c>: POS_OUT</c>) or writes whole (<c>DECLARE(PS)</c>)
/// is removed exactly, and one no range can remove alone (a macro writing it with other code) is
/// refused.</para>
///
/// <para><b>Every text DXC could be compiling.</b> The managed preprocessor does not define
/// DXC's own macros. A conditional that tests one (anything DXC was measured to predefine, see
/// <see cref="FxMacroPreprocessor.IsCompilerPredefined"/>, and any other reserved name the
/// source never <c>#define</c>s) is therefore evaluated BOTH ways: the view is built once per combination of
/// outcomes (at most <see cref="MaxViews"/>), DXC's real text is one of them, and the decision
/// must hold in all of them. A source none of whose views declares the entry twice is returned
/// unchanged; a blanked range must be proven in every view. A source whose view cannot be built
/// at all is read from its raw text (see <see cref="FromRawText"/>), which refuses whatever it
/// cannot pin down but only on evidence of a second declaration.</para>
///
/// <para><b>Fail-safe.</b> The crash kills the consumer's process, so no possibly-redeclared
/// entry reaches the SPIR-V back end. Two or more declaration-shaped uses of the entry name at
/// global scope, any of which cannot be read as a plain declaration (a parenthesised declarator,
/// <c>float4 (PS)(...)</c>, among them), are refused with <c>SD0236</c>. So is a call to the
/// entry function between its prototype and its definition (without the prototype the call has
/// no declaration; fxc compiles it), an overload prototype of an entry function the source
/// calls, a prototype whose return type is spelled differently from the definition's in a way
/// that cannot be told apart from DXC's own type identity, a declaration shape the reading does
/// not model (a second declarator, <c>float4 A(), PS(...);</c>, or a directive between the name
/// and its parameters), and (when the preprocessed view cannot be built) any prototype with a
/// macro in or around it, since no edit can be proven then. No definition or several, and a
/// prototype whose return type is a different BUILT-IN type, reach DXC, whose front end reports
/// them cleanly (<c>missing entry point definition</c>, <c>ambiguous entry point function</c>,
/// <c>functions that differ only in their return type</c>) before the back end runs.</para>
///
/// <para>The validated DXIL of DirectX 12 and the DXBC of DirectX 11 and FNA never had the
/// fault and keep their input as is (see <see cref="AppliesTo"/>). The input is changed only
/// when a prototype of the entry point is blanked; otherwise it is handed to DXC as it was (or
/// refused, which happens only where DXC may see the entry declared twice).</para>
/// </remarks>
internal static partial class DxcSpirvEntryRedeclarations
{
    /// <summary>The registered diagnostic code for an entry-point redeclaration that cannot be removed safely.</summary>
    public const string ErrorCode = "SD0236";

    /// <summary>
    /// True for a DXC compile that emits SPIR-V, the only output with the fault, and for an
    /// unvalidated (<c>-Vd</c>) DXIL compile: that is the OpenGL target's reflection companion,
    /// whose blob is discarded and which must read the same text as the SPIR-V compile it
    /// describes (otherwise an unused overload prototype fxc accepts compiles to SPIR-V and is
    /// "ambiguous" to the companion). Shipped DXIL (DirectX 12) is always validated, so it
    /// never comes through here.
    /// </summary>
    public static bool AppliesTo(IReadOnlyList<string> arguments) =>
        !arguments.Contains("-P", StringComparer.Ordinal)
        && (arguments.Contains("-spirv", StringComparer.Ordinal) || arguments.Contains("-Vd", StringComparer.Ordinal));

    /// <summary>
    /// The text DXC's SPIR-V compile of <paramref name="entryPoint"/> should be handed: the
    /// source unchanged, the source with the entry function's redundant prototypes blanked, or
    /// an <see cref="ErrorCode"/> refusal.
    /// </summary>
    /// <param name="source">The HLSL compiler input (flattened; macros not yet expanded).</param>
    /// <param name="entryPoint">The entry function DXC is asked to compile (after any macro: DXC's <c>-E</c>).</param>
    /// <param name="sourceFileName">Display name used in diagnostics.</param>
    /// <param name="macros">The <c>-D</c> macros the compile passes to DXC, so the view sees the same conditionals.</param>
    public static Result<string, ShaderError> Prepare(
        string source,
        string entryPoint,
        string? sourceFileName,
        IReadOnlyList<(string Name, string? Value)>? macros = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        string file = sourceFileName ?? string.Empty;

        // `-D` macros come before the text, on lines of their own that are dropped from the view.
        string prefix = string.Concat((macros ?? []).Select(m => $"#define {m.Name} {m.Value ?? "1"}\n"));
        // Line breaks as the preprocessor reads them (a lone '\r' is one), for the line-anchored searches.
        string lines = (prefix + source).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.IsNullOrEmpty(entryPoint) || CannotBeDeclaredTwice(lines, entryPoint))
            return Ok(source);

        var macroNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in DefinedName().Matches(lines))
            macroNames.Add(m.Groups[1].Value);

        Result<IReadOnlyList<View>, string> views = ViewsOf(prefix, source, file);
        return views.IsSuccess
            ? FromViews(source, views.Value, prefix, entryPoint, file, macroNames)
            : FromRawText(source, prefix, entryPoint, file, views.Error, macroNames);
    }

    // ---- The views: one per outcome of each conditional on a macro only DXC defines ---------

    /// <summary>The most views (combinations of outcomes of conditionals on DXC's macros) built for one source.</summary>
    private const int MaxViews = 16;

    /// <summary>
    /// One text DXC could be compiling: the preprocessed source (the <c>-D</c> lines dropped) with
    /// every conditional on a macro only DXC defines taking the outcome in <see cref="Outcomes"/>.
    /// </summary>
    /// <param name="Text">The preprocessed text, one line per source line.</param>
    /// <param name="Outcomes">The outcome of each such conditional, by its physical line in the prefixed input.</param>
    private sealed record View(string Text, IReadOnlyDictionary<int, bool> Outcomes);

    /// <summary>
    /// Every view of <paramref name="source"/> (see <see cref="View"/>), or why there is none to
    /// trust: the managed preprocessor failed on it, or too many combinations.
    /// </summary>
    private static Result<IReadOnlyList<View>, string> ViewsOf(string prefix, string source, string file)
    {
        string input = prefix + source;
        lock (ViewCache)
        {
            int hit = ViewCache.FindIndex(e => string.Equals(e.Input, input, StringComparison.Ordinal) && string.Equals(e.File, file, StringComparison.Ordinal));
            if (hit >= 0)
                return ViewCache[hit].Views;
        }

        int prefixLines = prefix.Count(c => c == '\n');

        Result<IReadOnlyList<View>, string> result = Enumerate();
        lock (ViewCache)
        {
            ViewCache.Insert(0, (input, file, result));
            if (ViewCache.Count > ViewCacheSize)
                ViewCache.RemoveAt(ViewCache.Count - 1);
        }
        return result;

        Result<IReadOnlyList<View>, string> Enumerate()
        {
            var views = new List<View>();
            var pending = new Stack<Dictionary<int, bool>>();
            pending.Push([]);
            var tested = new List<string>();
            while (pending.Count > 0)
            {
                Dictionary<int, bool> forced = pending.Pop();
                Result<FxMacroPreprocessor.CompilerInput, ShaderError> view =
                    FxMacroPreprocessor.ProcessForCompiler(input, file, forced);
                if (view.IsFailure)
                {
                    return Result<IReadOnlyList<View>, string>.Fail(
                        $"the preprocessed view could not be built: {view.Error.Code} at line {view.Error.Line} of {view.Error.File}");
                }

                // Each conditional on DXC's macros the view did not choose an outcome for took its
                // natural one; the views where it takes the other are still to be built (with the
                // ones before it fixed, so no combination is built twice).
                var outcomes = new Dictionary<int, bool>(forced);
                foreach (FxMacroPreprocessor.UnknownConditional c in view.Value.UnknownConditionals ?? [])
                {
                    if (!tested.Contains(c.Name))
                        tested.Add(c.Name);
                    if (!forced.ContainsKey(c.PhysicalLine))
                    {
                        pending.Push(new Dictionary<int, bool>(outcomes) { [c.PhysicalLine] = !c.Natural });
                        outcomes[c.PhysicalLine] = c.Natural;
                    }
                }
                views.Add(new View(DropLines(view.Value.Text, prefixLines), outcomes));
                if (views.Count + pending.Count > MaxViews)
                {
                    return Result<IReadOnlyList<View>, string>.Fail(
                        $"conditionals on {string.Join(", ", tested.Select(n => $"'{n}'"))}, which only the compiler defines, " +
                        $"select among more than {MaxViews} texts");
                }
            }
            return Result<IReadOnlyList<View>, string>.Ok(views);
        }
    }

    /// <summary>The last few sources' views: an OpenGL compile prepares the same input for its SPIR-V compile and its reflection companion, per stage.</summary>
    private static readonly List<(string Input, string File, Result<IReadOnlyList<View>, string> Views)> ViewCache = [];

    private const int ViewCacheSize = 4;

    // ---- The decision on the views ----------------------------------------------------------

    private static Result<string, ShaderError> FromViews(
        string source, IReadOnlyList<View> views, string prefix, string entryPoint, string file, IReadOnlySet<string> macroNames)
    {
        // What each view needs: nothing (fewer than two declarations, or a shape DXC's front end
        // reports itself), its prototypes blanked, or a refusal.
        var plans = new List<(View View, IReadOnlyList<Declaration> Plan)>();
        foreach (View view in views)
        {
            Analysis analysis = Analyze(view.Text, entryPoint, rawText: false);
            if (analysis.DeclarationShaped < 2)
            {
                plans.Add((view, []));
                continue;
            }
            if (analysis.Unread.Count > 0)
                return Fail(Unread(view.Text, file, entryPoint, analysis.Unread[0]));

            Result<IReadOnlyList<Declaration>, ShaderError> plan = Plan(analysis, view.Text, file, entryPoint);
            if (plan.IsFailure)
                return Fail(plan.Error);
            plans.Add((view, plan.Value));
        }
        if (plans.All(p => p.Plan.Count == 0))
            return Ok(source);

        var raw = new LineIndex(source);
        ILookup<int, Tok> rawByLine = Tokenize(source, rawText: true)
            .Where(t => t.Kind is not (TokKind.Comment or TokKind.Directive))
            .ToLookup(t => raw.Position(t.Offset).Line);
        var located = new List<Located>();
        foreach ((View view, IReadOnlyList<Declaration> plan) in plans)
        {
            var expanded = new LineIndex(view.Text);
            List<Tok> viewTokens = Tokenize(view.Text, rawText: false);
            located.Add(new Located(view, plan, viewTokens, Without(viewTokens, plan), []));
            if (plan.Count == 0)
                continue;
            if (raw.LineCount != expanded.LineCount)
                return Fail(NotLocated(view.Text, file, entryPoint, plan[0]));
            Dictionary<int, int> viewIndex = viewTokens.Select((t, i) => (t.Offset, i)).ToDictionary(p => p.Offset, p => p.i);
            ILookup<int, (Tok Token, int Index)> viewByLine = viewTokens.Select((t, i) => (t, i)).ToLookup(p => expanded.Position(p.t.Offset).Line);
            foreach (Declaration prototype in plan)
                located[^1].Candidates.Add(Candidates(rawByLine, viewTokens, viewIndex, viewByLine, expanded, prototype, entryPoint, macroNames));
        }

        // The common case first, proven with one preprocess per view: every prototype's lines
        // untouched by any macro, so each range is the positional one.
        if (located.SelectMany(l => l.Candidates).All(c => c.Positional) && TryAll(source, prefix, file, located, l => l.Candidates.Select(c => c.Ranges[0])) is { } fast)
            return Ok(fast);

        // Otherwise each prototype's range is searched for on its own view (a range another view
        // already chose first), then all of them proven together in every view (DXC gets them at
        // once). The positional range may suit one view and not another (a macro on the
        // prototype's line that expands to code in one view only), so a second pass tries the
        // narrowest ranges first.
        Located firstPlanned = located.First(l => l.Plan.Count > 0);
        foreach (bool narrowestFirst in new[] { false, true })
        {
            var spans = new List<(int Start, int End)>();
            foreach (Located l in located)
            {
                var inView = new List<(int Start, int End)>();
                for (int i = 0; i < l.Plan.Count; i++)
                {
                    IEnumerable<(int Start, int End)> ordered = narrowestFirst
                        ? l.Candidates[i].Ranges.OrderByDescending(r => r.Start).ThenBy(r => r.End)
                        : l.Candidates[i].Ranges;
                    List<(int Start, int End)> ranges = ordered.OrderBy(r => spans.Contains(r) ? 0 : 1).ToList();
                    (int Start, int End)? span = Search(source, prefix, file, l, l.Plan[i], ranges, inView);
                    if (span is null)
                        return Fail(NotLocated(l.View.Text, file, entryPoint, l.Plan[i]));
                    inView.Add(span.Value);
                }
                spans.AddRange(inView);
            }
            if (TryAll(source, prefix, file, located, _ => spans) is { } blanked)
                return Ok(blanked);
            if (views.Count == 1)
                break;
        }
        return Fail(NotLocated(firstPlanned.View.Text, file, entryPoint, firstPlanned.Plan[0]));
    }

    /// <summary>A view, the prototypes to blank in it, its tokens, the tokens it must have once they are blanked, and each prototype's candidate ranges.</summary>
    private sealed record Located(
        View View, IReadOnlyList<Declaration> Plan, List<Tok> ViewTokens, List<Tok> Expected, List<CandidateRanges> Candidates);

    /// <summary>The raw ranges that could be a prototype, most likely first; <see cref="Positional"/> when the first is the positional one.</summary>
    private sealed record CandidateRanges(List<(int Start, int End)> Ranges, bool Positional);

    /// <summary>
    /// The source with every range the views' prototypes select blanked, when that is proven in
    /// every view to remove exactly that view's prototypes; null otherwise.
    /// </summary>
    private static string? TryAll(
        string source, string prefix, string file, List<Located> located, Func<Located, IEnumerable<(int Start, int End)>> ranges)
    {
        List<(int Start, int End)> spans = located.SelectMany(ranges).Distinct().ToList();
        if (spans.Count == 0)
            return null;
        string blanked = Blank(source, spans);
        return located.All(l => Verify(blanked, prefix, file, l.View, l.Expected)) ? blanked : null;
    }

    private static ShaderError NotLocated(string text, string file, string entryPoint, Declaration prototype) =>
        Refuse(text, file, prototype.NameLine, prototype.NameColumn,
            $"entry point '{entryPoint}' is declared more than once, and this declaration cannot be removed from the " +
            "source as written without changing anything else the compiler sees: a macro writes, renames or moves part " +
            "of it together with other code, or a conditional on a macro only the compiler defines changes what the " +
            "edit would take. " + CrashSentence + $" Write the prototype of '{entryPoint}' out on lines of its own " +
            "(macros inside it are fine), or remove it.");

    /// <summary>The most raw ranges <see cref="Search"/> tries for one prototype before refusing.</summary>
    private const int MaxLocateAttempts = 32;

    /// <summary>
    /// The candidate raw ranges for <paramref name="prototype"/> (read off a view): each runs from
    /// the start of a raw token on the prototype's first line to the end of a raw token on its last
    /// line (exact token boundaries, never part of a token). The positional candidate (the two
    /// lines untouched by any macro, so the prototype's first and last tokens sit at the same token
    /// positions in the raw lines as in the expanded ones) comes first, then every plausible pair,
    /// narrowest first: a start that is the prototype's first token or a macro name, an end that is
    /// its last token, a macro name, or a macro call's ')'. Starts at or before the last raw spelling
    /// of the entry's name on that line come first: one after it (a macro name after the prototype)
    /// can begin it only if a macro writes the name, so it is tried last rather than spending the
    /// attempts.
    /// </summary>
    private static CandidateRanges Candidates(
        ILookup<int, Tok> rawByLine, List<Tok> viewTokens, Dictionary<int, int> viewIndex,
        ILookup<int, (Tok Token, int Index)> viewByLine, LineIndex expanded,
        Declaration prototype, string entryPoint, IReadOnlySet<string> macroNames)
    {
        var candidates = new List<(int Start, int End)>();
        int first = viewIndex.TryGetValue(prototype.Start, out int f) ? f : -1;
        int last = first;
        while (last + 1 < viewTokens.Count && viewTokens[last + 1].Offset < prototype.End)
            last++;
        // A compiler-counted macro would count differently once part of the text is gone.
        if (first < 0 || last < first || viewTokens.Skip(first).Take(last - first + 1).Any(t => t.IsWord("__COUNTER__")))
            return new CandidateRanges(candidates, false);

        int startLine = expanded.Position(viewTokens[first].Offset).Line;
        int endLine = expanded.Position(viewTokens[last].Offset).Line;
        List<Tok> onStartLine = rawByLine[startLine].ToList();
        List<Tok> onEndLine = rawByLine[endLine].ToList();

        int firstInLine = viewByLine[startLine].Count(p => p.Index < first);
        int lastInLine = viewByLine[endLine].Count(p => p.Index < last);
        bool positional = firstInLine < onStartLine.Count && lastInLine < onEndLine.Count;
        if (positional)
            candidates.Add((onStartLine[firstInLine].Offset, onEndLine[lastInLine].Offset + onEndLine[lastInLine].Text.Length));

        int bound = prototype.NameLine == startLine && onStartLine.LastOrDefault(t => t.IsWord(entryPoint)) is { Text: not null } name
            ? name.Offset
            : int.MaxValue;
        bool IsMacro(Tok t) => t.Kind == TokKind.Identifier && macroNames.Contains(t.Text);
        foreach (Tok s in onStartLine.Where(t => SameToken(t, viewTokens[first]) || IsMacro(t)).OrderBy(t => t.Offset <= bound ? 0 : 1).ThenByDescending(t => t.Offset))
        {
            foreach (Tok e in onEndLine.Where(t => t.Offset >= s.Offset && (SameToken(t, viewTokens[last]) || t.Is(")") || IsMacro(t))))
                candidates.Add((s.Offset, e.Offset + e.Text.Length));
        }

        List<(int Start, int End)> usable = candidates.Distinct()
            .Where(c => c.End > c.Start && !Enumerable.Range(startLine, endLine - startLine + 1).SelectMany(l => rawByLine[l]).Any(t => t.Offset >= c.Start && t.Offset < c.End && t.IsWord("__COUNTER__")))
            .ToList();
        return new CandidateRanges(usable, positional && usable.Count > 0 && usable[0] == candidates[0]);
    }

    /// <summary>
    /// The first of <paramref name="candidates"/> whose removal takes exactly
    /// <paramref name="prototype"/>'s tokens out of its view and nothing else, proven by
    /// preprocessing the source with the range blanked (the same <c>-D</c> macros, the same
    /// outcomes of the conditionals on DXC's macros); null when none is. So a range that would
    /// also take a macro call before the prototype (<c>DECL_PARAM(Unused) float4 PS(...);</c>: the
    /// call's <c>float4 Unused;</c> would vanish with it), a directive, or another part of a
    /// macro's arguments is never accepted, while a prototype that a macro completes
    /// (<c>: POS_OUT</c>) or writes whole (a <c>DECLARE(PS)</c> call that produces nothing else) is.
    /// </summary>
    private static (int Start, int End)? Search(
        string source, string prefix, string file, Located located, Declaration prototype, IReadOnlyList<(int Start, int End)> candidates,
        List<(int Start, int End)> taken)
    {
        List<Tok> expected = Without(located.ViewTokens, [prototype]);
        // Two prototypes with the same tokens prove each other's range: one already taken is not this one's.
        foreach ((int start, int end) in candidates.Where(c => !taken.Any(t => c.Start < t.End && t.Start < c.End)).Take(MaxLocateAttempts))
        {
            if (Verify(Blank(source, [(start, end)]), prefix, file, located.View, expected))
                return (start, end);
        }
        return null;
    }

    private static bool SameToken(Tok a, Tok b) => a.Kind == b.Kind && string.Equals(a.Text, b.Text, StringComparison.Ordinal);

    /// <summary>The preprocessed tokens with every listed declaration's tokens taken out.</summary>
    private static List<Tok> Without(List<Tok> viewTokens, IReadOnlyList<Declaration> removed)
    {
        List<Declaration> spans = removed.OrderBy(d => d.Start).ToList();
        var kept = new List<Tok>(viewTokens.Count);
        int next = 0;
        foreach (Tok t in viewTokens)
        {
            while (next < spans.Count && spans[next].End <= t.Offset)
                next++;
            if (next >= spans.Count || t.Offset < spans[next].Start)
                kept.Add(t);
        }
        return kept;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> preprocesses, as <paramref name="view"/> was built
    /// (the same conditionals on DXC's macros taking the same outcomes, and no other), to exactly
    /// the <paramref name="expected"/> token stream: the same kinds and spellings in the same
    /// order (directives the view passes through included).
    /// </summary>
    private static bool Verify(string candidate, string prefix, string file, View view, List<Tok> expected)
    {
        Result<FxMacroPreprocessor.CompilerInput, ShaderError> result =
            FxMacroPreprocessor.ProcessForCompiler(prefix + candidate, file, view.Outcomes);
        if (result.IsFailure || (result.Value.UnknownConditionals ?? []).Any(c => !view.Outcomes.ContainsKey(c.PhysicalLine)))
            return false;
        List<Tok> actual = Tokenize(DropLines(result.Value.Text, prefix.Count(c => c == '\n')), rawText: false);
        if (actual.Count != expected.Count)
            return false;
        for (int i = 0; i < actual.Count; i++)
        {
            if (!SameToken(actual[i], expected[i]))
                return false;
        }
        return true;
    }

    // ---- Without a view: the raw text alone, refusing whatever it cannot pin down ----------

    /// <summary>
    /// The decision when no view can be trusted (<paramref name="why"/>): on the raw text, which
    /// shows every declaration written out (in every branch) but not one a macro writes. A source
    /// is refused only on evidence that DXC may see the entry declared twice: two declaration-shaped
    /// uses, counting each use of a macro that may write the entry's name.
    /// </summary>
    private static Result<string, ShaderError> FromRawText(
        string source, string prefix, string entryPoint, string file, string why, IReadOnlySet<string> macroNames)
    {
        Analysis analysis = Analyze(source, entryPoint, rawText: true);
        List<Tok> tokens = Tokenize(source, rawText: true).Where(t => t.Kind != TokKind.Comment).ToList();
        List<Tok> defineTokens = Tokenize(prefix + source, rawText: true)
            .Where(t => t.Kind == TokKind.Directive && string.Equals(DirectiveKeyword(t.Text), "define", StringComparison.Ordinal))
            .ToList();
        bool unbalancedMacro = defineTokens.Any(t => !Balanced(t.Text));

        // Uses of a macro that may write the entry's name; one that may write it more than once counts twice.
        (int writers, Token? firstWriter) = MacroWriters(source, tokens, defineTokens, entryPoint, unbalancedMacro);
        if (analysis.DeclarationShaped + writers < 2)
            return Ok(source);
        if (firstWriter is not null)
        {
            return Fail(Refuse(source, file, firstWriter.Line, firstWriter.Column,
                $"entry point '{entryPoint}' may be declared more than once through this macro, and the source cannot be " +
                $"preprocessed here to tell ({why}). {CrashSentence} Write the declarations of '{entryPoint}' out without " +
                "a macro, or remove its prototypes."));
        }
        if (analysis.Unread.Count > 0)
            return Fail(Unread(source, file, entryPoint, analysis.Unread[0], why));
        if (analysis.Prototypes.Count == 0)
            return Ok(source);

        // Several written definitions cannot be told apart without the view (one may sit in a
        // dead #if branch, which would leave DXC one definition and the crash).
        if (analysis.Definitions.Count > 1)
        {
            Declaration prototype = analysis.Prototypes[0];
            return Fail(Refuse(source, file, prototype.NameLine, prototype.NameColumn,
                $"entry point '{entryPoint}' has a prototype and more than one written definition, and which definition is " +
                $"compiled cannot be read from this source ({why}). {CrashSentence} Remove the prototype of '{entryPoint}'."));
        }

        Result<IReadOnlyList<Declaration>, ShaderError> plan = Plan(analysis, source, file, entryPoint);
        if (plan.IsFailure)
            return Fail(plan.Error);
        if (plan.Value.Count == 0)
            return Ok(source);

        // Without a view no edit can be proven, so a range is blanked only when its text reaches
        // the compiler exactly as written: no macro name inside it, not inside a macro call's
        // parentheses, and no macro body that could open or close one around it.
        foreach (Declaration prototype in plan.Value)
        {
            int parens = 0;
            foreach (Tok t in tokens.TakeWhile(t => t.Offset < prototype.Start))
            {
                if (t.Is("("))
                    parens++;
                else if (t.Is(")"))
                    parens--;
            }
            bool macroInside = tokens.Any(t => t.Offset >= prototype.Start && t.Offset < prototype.End
                && t.Kind == TokKind.Identifier && (macroNames.Contains(t.Text) || t.Text == "__COUNTER__"));
            if (parens != 0 || macroInside || unbalancedMacro)
            {
                return Fail(Refuse(source, file, prototype.NameLine, prototype.NameColumn,
                    $"entry point '{entryPoint}' is declared more than once, and this source cannot be preprocessed here " +
                    $"({why}), so whether removing the prototype changes anything else cannot be proven: a macro is used in " +
                    $"or around it. {CrashSentence} Write the prototype of '{entryPoint}' without macros, or remove it."));
            }
        }
        return Ok(Blank(source, plan.Value.Select(d => (d.Start, d.End)).ToList()));
    }

    /// <summary>
    /// The uses, at global scope, of a macro that may write <paramref name="entryPoint"/>: one
    /// whose body names it (or names such a macro), and one that pastes tokens (<c>##</c>) into a
    /// name that could spell it (or such a macro) from the pieces it has at that use (its body's
    /// words and the call's arguments). Each counts once, or twice when it may write the name
    /// more than once. Returns the count and the first such use. With a macro body that opens or
    /// closes a brace, uses at any depth count, since the depth cannot be read.
    /// </summary>
    private static (int Count, Token? First) MacroWriters(
        string source, List<Tok> tokens, List<Tok> defineTokens, string entryPoint, bool anyDepth)
    {
        var defines = new Dictionary<string, (bool FunctionLike, List<Tok> Body)>(StringComparer.Ordinal);
        foreach (Tok d in defineTokens)
        {
            Match m = DefineParts().Match(d.Text.Replace("\\\r\n", " ", StringComparison.Ordinal).Replace("\\\n", " ", StringComparison.Ordinal).Replace("\\\r", " ", StringComparison.Ordinal));
            if (m.Success)
                defines[m.Groups[1].Value] = (m.Groups[2].Success, Tokenize(m.Groups[3].Value, rawText: true).Where(t => t.Kind != TokKind.Comment).ToList());
        }

        // Naming: the body names the entry or a naming macro (to a fixed point). Pasting: `##`.
        var naming = new HashSet<string>(StringComparer.Ordinal);
        var pasting = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, (bool _, List<Tok> body)) in defines)
        {
            for (int i = 0; i + 1 < body.Count; i++)
            {
                if (body[i].Is("#") && body[i + 1].Is("#") && body[i + 1].Offset == body[i].Offset + 1)
                    pasting.Add(name);
            }
        }
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach ((string name, (bool _, List<Tok> body)) in defines)
            {
                if (!naming.Contains(name) && body.Any(t => t.Kind == TokKind.Identifier && (t.Text == entryPoint || naming.Contains(t.Text))))
                    grew = naming.Add(name) | grew;
            }
        }
        // A macro whose body uses a pasting one may paste anything: it counts as one that may write the name.
        var indirect = new HashSet<string>(StringComparer.Ordinal);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach ((string name, (bool _, List<Tok> body)) in defines)
            {
                if (!naming.Contains(name) && !pasting.Contains(name) && !indirect.Contains(name)
                    && body.Any(t => t.Kind == TokKind.Identifier && (pasting.Contains(t.Text) || indirect.Contains(t.Text))))
                {
                    grew = indirect.Add(name) | grew;
                }
            }
        }
        var targets = new HashSet<string>(naming.Concat(pasting).Concat(indirect), StringComparer.Ordinal) { entryPoint };

        var lines = new LineIndex(source);
        int count = 0;
        Token? first = null;
        int depth = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            Tok t = tokens[i];
            if (t.Is("{"))
                depth++;
            else if (t.Is("}"))
                depth = Math.Max(0, depth - 1);
            if (t.Kind != TokKind.Identifier || (depth > 0 && !anyDepth) || !defines.TryGetValue(t.Text, out var define))
                continue;

            int writes;
            if (naming.Contains(t.Text))
            {
                writes = define.Body.Count(b => b.Kind == TokKind.Identifier && (b.Text == entryPoint || naming.Contains(b.Text))) > 1 ? 2 : 1;
            }
            else if (indirect.Contains(t.Text))
            {
                writes = 2;
            }
            else if (pasting.Contains(t.Text))
            {
                // The pieces a paste at this use can join: the body's words and the call's arguments.
                var pieces = new HashSet<string>(define.Body.Where(b => b.Kind is TokKind.Identifier or TokKind.Number).Select(b => b.Text), StringComparer.Ordinal);
                if (define.FunctionLike && i + 1 < tokens.Count && tokens[i + 1].Is("("))
                {
                    int parens = 0;
                    for (int j = i + 1; j < tokens.Count; j++)
                    {
                        parens += tokens[j].Is("(") ? 1 : tokens[j].Is(")") ? -1 : 0;
                        if (parens == 0)
                            break;
                        if (tokens[j].Kind is TokKind.Identifier or TokKind.Number)
                            pieces.Add(tokens[j].Text);
                    }
                }
                writes = targets.Any(target => CanSpell(target, pieces)) ? 2 : 0;
            }
            else
            {
                continue;
            }

            if (writes == 0)
                continue;
            count += writes;
            if (first is null)
            {
                (int line, int column) = lines.Position(t.Offset);
                first = new Token(line, column + 1, t.Offset);
            }
        }
        return (count, first);
    }

    /// <summary>True when <paramref name="word"/> is a concatenation of <paramref name="pieces"/> (each usable any number of times).</summary>
    private static bool CanSpell(string word, IReadOnlySet<string> pieces)
    {
        var reachable = new bool[word.Length + 1];
        reachable[0] = true;
        for (int i = 0; i < word.Length; i++)
        {
            if (!reachable[i])
                continue;
            foreach (string piece in pieces)
            {
                if (piece.Length > 0 && i + piece.Length <= word.Length && string.CompareOrdinal(word, i, piece, 0, piece.Length) == 0)
                    reachable[i + piece.Length] = true;
            }
        }
        return reachable[word.Length];
    }

    /// <summary>True when a <c>#define</c> line's parentheses and braces balance (a body that opens or closes one does not).</summary>
    private static bool Balanced(string directive)
    {
        int parens = 0;
        int braces = 0;
        // Continuation lines joined, so a continued line starting with '#' is not read as a directive.
        foreach (Tok t in Tokenize(directive.TrimStart().TrimStart('#').Replace('\n', ' ').Replace('\r', ' '), rawText: true))
        {
            parens += t.Is("(") ? 1 : t.Is(")") ? -1 : 0;
            braces += t.Is("{") ? 1 : t.Is("}") ? -1 : 0;
            if (parens < 0 || braces < 0)
                return false;
        }
        return parens == 0 && braces == 0;
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
    /// A cheap, exact gate: two declarations need the name written twice, or written once in a
    /// directive (an object-like macro used twice), or once as the argument of a function-like
    /// macro (which may write its parameter twice), or a token paste.
    /// </summary>
    private static bool CannotBeDeclaredTwice(string source, string name)
    {
        if (source.Contains("##", StringComparison.Ordinal))
            return false;
        int first = IndexOfWord(source, name, 0);
        if (first < 0)
            return true;
        if (IndexOfWord(source, name, first + name.Length) >= 0 || FunctionLikeDefine().IsMatch(source))
            return false;
        int lineStart = source.LastIndexOf('\n', first) + 1;
        return !source.AsSpan(lineStart, first - lineStart).TrimStart().StartsWith("#");
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

    private static string DropLines(string text, int count)
    {
        int offset = 0;
        for (int i = 0; i < count && offset >= 0; i++)
            offset = text.IndexOf('\n', offset) is int n and >= 0 ? n + 1 : -1;
        return offset > 0 ? text[offset..] : text;
    }

    /// <summary>
    /// Replaces every character of each range with spaces, keeping line breaks: as many spaces as
    /// the character's UTF-8 bytes, because DXC counts columns in bytes of its UTF-8 input, so a
    /// later diagnostic on the same line keeps its column.
    /// </summary>
    private static string Blank(string source, IReadOnlyList<(int Start, int End)> spans)
    {
        var blanked = new bool[source.Length];
        foreach ((int start, int end) in spans)
            Array.Fill(blanked, true, start, end - start);

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

    private static Analysis Analyze(string text, string name, bool rawText)
    {
        List<Tok> tokens = Tokenize(text, rawText).Where(t => t.Kind != TokKind.Comment).ToList();
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

                // Otherwise, in the view every macro is expanded, so a use without '(' declares
                // nothing. In raw text it may be a macro's argument (`DECLARE(PS)`).
                if (rawText && depth == 0)
                    unread.Add(at);
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

    private static string DirectiveKeyword(string directive)
    {
        Match m = DirectiveName().Match(directive);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

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

        /// <summary>The 1-based line and 0-based column of <paramref name="offset"/>.</summary>
        public (int Line, int Column) Position(int offset)
        {
            int index = _starts.BinarySearch(offset);
            int line = index >= 0 ? index : ~index - 1;
            return (line + 1, offset - _starts[line]);
        }
    }

    [GeneratedRegex(@"^\s*#\s*([A-Za-z_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex DirectiveName();

    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+[A-Za-z_][A-Za-z0-9_]*\(", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex FunctionLikeDefine();

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

    /// <summary>A <c>#define</c> line (continuations joined): its name, its parameter list if function-like, its body.</summary>
    [GeneratedRegex(@"^\s*#\s*define\s+([A-Za-z_][A-Za-z0-9_]*)(\([^)]*\))?(.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex DefineParts();

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
