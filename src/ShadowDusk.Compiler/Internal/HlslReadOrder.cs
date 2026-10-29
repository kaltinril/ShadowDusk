#nullable enable

using System.Text.RegularExpressions;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// The order in which an entry point first reads each LEGACY sampler's texture, the order fxc
/// numbers legacy textures in (<see cref="DxTextureSlotOrder"/>), taken from the preprocessed
/// SOURCE.
///
/// <para><b>Why the source.</b> No compiled instruction stream carries fxc's order:
/// d3dcompiler_47 schedules its reads (a read feeding <c>clip</c> first, independent reads
/// reordered), and vkd3d-shader 2.1's parser swaps the operands of <c>&gt;</c> and <c>&lt;=</c>
/// (<c>a &gt; b</c> becomes <c>LESS(b, a)</c>, <c>a &lt;= b</c> becomes <c>GEQUAL(b, a)</c>,
/// <c>hlsl.y</c>), emitting the right-hand read first. Both gave wrong slots, measured.</para>
///
/// <para><b>The model</b> (measured against <c>mgfxc</c>): fxc numbers in EVALUATION order, which in
/// HLSL is token order with these exceptions: a read <c>T.Sample(S, uv)</c> (or a legacy
/// <c>tex2D(S, uv)</c> the rewrite did not reach) happens after its arguments; a call to a user
/// function evaluates its arguments, then the function's body; a <c>switch</c> numbers its
/// <c>default</c> arm after every <c>case</c> arm, wherever it is written; and a read after an
/// unconditional <c>return</c>, <c>break</c> or <c>continue</c> in the same block is never numbered
/// (fxc removes it; a read after <c>discard</c> is numbered). Both arms of a <c>?:</c> are read
/// whatever the condition. fxc numbers after it has inlined, unrolled loops and folded the
/// conditions it can evaluate, but before algebraic dead-code removal (the read in
/// <c>tex2D(B, uv) * 0</c> still counts where it is).</para>
///
/// <para><b>Which conditions fxc folds is not modelled, at all.</b> It inlines and folds far
/// beyond any syntactic test (a call returning a constant, a default or <c>out</c> argument, one
/// component of a vector, <c>k &gt; 0 &amp;&amp; false</c>, a uniform the shader assigns, a
/// <c>uint</c> compared with <c>0</c>), measured, and every attempt to prove a condition
/// "certainly run-time" was broken by a new shape. So every read under an <c>if</c>/<c>else</c>,
/// in a <c>switch</c> arm, in a loop that is not certainly entered, in a later iteration of any
/// loop, or after a <c>return</c>, <c>break</c> or <c>continue</c> that might not run, is a MAYBE
/// read: fxc may number its texture there, or not. Each such construct is a GUARD, and a read is
/// kept only if every guard it sits under is; the guards are taken as independent (fxc could fold
/// each either way), which can only add orders, never hide one. Reads that share their guards
/// share their fate: everything after one early <c>return</c> stays or goes together. The order
/// is trusted only when it is the same whichever guards fxc keeps (for every pair of textures, no
/// choice of guards keeps a read of the second while dropping every read of the first before it
/// and keeping a later one); otherwise the walk REFUSES. A loop is certainly entered only
/// when it is a <c>do</c> loop, or a <c>for</c> loop whose counter is a signed <c>int</c>
/// initialised from an integer literal and compared (<c>&lt; &lt;= &gt; &gt;= !=</c>) with an integer
/// literal or a <c>static const int</c> initialised from one, the first test holding in exact
/// integer arithmetic.</para>
///
/// <para>The walk also refuses: the entry point not found or overloaded, a call to an overloaded
/// function, a legacy texture used other than as the object of a method call, a legacy sampler
/// passed through a parameter, a texture the shader binds that the walk never saw read, a read in a
/// <c>for</c> loop's increment clause, a <c>switch</c> arm that falls through, recursion, a
/// statement it cannot parse, unbalanced brackets. The caller reports a refusal as <c>SD0230</c>
/// and keeps the backend's slots.</para>
/// </summary>
internal static class HlslReadOrder
{
    private static readonly Regex LegacyIntrinsic = new(@"^tex(1D|2D|3D|CUBE)(bias|grad|lod|proj)?$", RegexOptions.CultureInvariant);

    /// <summary>A decimal integer literal with no suffix (a leading 0 would be octal).</summary>
    private static readonly Regex IntegerLiteral = new(@"^(0|[1-9][0-9]{0,9})$", RegexOptions.CultureInvariant);

    /// <summary>Identifiers that can precede a name without declaring it.</summary>
    private static readonly HashSet<string> NotTypes = new(StringComparer.Ordinal) { "return", "else", "case", "do" };

    /// <summary>Keywords that start a statement; met inside an expression, the statement was misparsed.</summary>
    private static readonly HashSet<string> StatementKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "do", "switch", "case", "default", "return", "break", "continue",
    };

    /// <summary>The comparisons a certainly-entered <c>for</c> loop may test its counter with.</summary>
    private static readonly HashSet<string> CounterTests = new(StringComparer.Ordinal) { "<", "<=", ">", ">=", "!=" };

    /// <summary>Tokens walked before the walk gives up (a call tree that repeats a large helper).</summary>
    private const int Budget = 4_000_000;

    private enum Kind { Ident, Number, Punct, String }

    private readonly record struct Tok(Kind Kind, string Text);

    private sealed record Fn(string Name, int ParamOpen, int BodyStart, int BodyEnd);

    /// <summary>Reads recorded before the walk gives up (the order check is cubic in one texture's reads).</summary>
    private const int MaxReads = 20_000;

    /// <summary>
    /// What a <c>return</c>, <c>break</c> or <c>continue</c> leaves: one walk of a function body
    /// (the target of <c>return</c>), a loop (of <c>break</c> and <c>continue</c>) or a switch arm
    /// (of <c>break</c>). <see cref="Truncations"/>: one guard per terminator met in it, which every
    /// later read inside it sits under.
    /// </summary>
    private sealed class Scope
    {
        public required Scope? Parent { get; init; }
        public required bool IsFrame { get; init; }
        public required bool IsLoop { get; init; }
        public List<int> Truncations { get; } = new();
    }

    /// <summary>A guard path, innermost first.</summary>
    private sealed record Guards(int Id, Guards? Next);

    /// <param name="Path">The guards of the branches, switch arms and loops around this point.</param>
    /// <param name="Scope">The innermost scope a terminator leaves.</param>
    private readonly record struct Ctx(Guards? Path, Scope Scope);

    /// <summary>
    /// The legacy texture names in first-read order for <paramref name="entryPoint"/>, or <c>null</c>
    /// with <paramref name="refusal"/> saying why when the order cannot be determined exactly.
    /// </summary>
    /// <param name="preprocessedSource">The compiled HLSL with macros expanded and dead <c>#if</c> arms removed.</param>
    /// <param name="entryPoint">The entry function.</param>
    /// <param name="legacyTextures">The textures legacy samplers sample through (rewritten reads <c>T.Sample(…)</c>).</param>
    /// <param name="legacySamplerTextures">Legacy sampler -> its texture (reads the rewrite did not reach, <c>tex2D(S, …)</c>).</param>
    /// <param name="present">The legacy textures the compiled shader still binds; only their order matters, and every one must be seen read.</param>
    /// <param name="refusal">Why the order could not be determined, when the result is <c>null</c>.</param>
    public static IReadOnlyList<string>? FirstReads(
        string preprocessedSource,
        string entryPoint,
        IReadOnlySet<string> legacyTextures,
        IReadOnlyDictionary<string, string> legacySamplerTextures,
        IReadOnlySet<string> present,
        out string? refusal)
    {
        refusal = null;
        List<Tok> code = Tokenize(preprocessedSource);
        int[]? match = MatchBrackets(code);
        if (match is null)
        {
            refusal = "unbalanced brackets";
            return null;
        }

        // ---- Top level: functions, and `static const int N = <literal>;` (a loop bound) ---------
        var functions = new Dictionary<string, List<(int ParamOpen, int BodyStart, int BodyEnd)>>(StringComparer.Ordinal);
        var staticConstInts = new Dictionary<string, long?>(StringComparer.Ordinal); // null: declared twice
        int depth = 0;
        int statementStart = 0;
        for (int i = 0; i < code.Count; i++)
        {
            Tok t = code[i];
            if (t.Kind == Kind.Punct && t.Text == "{") { depth++; continue; }
            if (t.Kind == Kind.Punct && t.Text == "}") { depth--; if (depth == 0) statementStart = i + 1; continue; }
            if (t.Kind == Kind.Punct && t.Text == ";" && depth == 0)
            {
                StaticConstInt(statementStart, i);
                statementStart = i + 1;
                continue;
            }
            if (t.Kind != Kind.Ident || depth != 0 || i + 1 >= code.Count || !Is(code[i + 1], "("))
                continue;
            int close = match[i + 1];
            int j = close + 1;
            while (j + 1 < code.Count && Is(code[j], ":") && code[j + 1].Kind == Kind.Ident)
                j += 2; // a semantic
            if (j < code.Count && Is(code[j], "{"))
            {
                if (!functions.TryGetValue(t.Text, out var list))
                    functions[t.Text] = list = new();
                list.Add((i + 1, j + 1, match[j]));
                i = match[j];
                statementStart = i + 1;
                continue;
            }
            i = close;
        }

        if (!functions.TryGetValue(entryPoint, out var entry) || entry.Count != 1)
        {
            refusal = $"the entry point '{entryPoint}' was not found exactly once";
            return null;
        }

        // Every read in evaluation order, with the guards it sits under (empty: certainly kept).
        var occurrences = new List<(string Texture, int[] Guards)>();
        var walking = new HashSet<string>(StringComparer.Ordinal);
        var localNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        int budget = Budget;
        int nextGuard = 0;
        string? why = null;

        var entryFn = new Fn(entryPoint, entry[0].ParamOpen, entry[0].BodyStart, entry[0].BodyEnd);
        if (!WalkFunction(entryFn, null))
        {
            refusal = why ?? "the entry point's evaluation order is outside the model";
            return null;
        }

        // ---- Order, and the refusal when the guards fxc keeps could change it -------------------
        var reads = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int k = 0; k < occurrences.Count; k++)
        {
            if (!present.Contains(occurrences[k].Texture))
                continue;
            if (!reads.TryGetValue(occurrences[k].Texture, out var at))
                reads[occurrences[k].Texture] = at = new List<int>();
            at.Add(k);
        }
        string? unseen = present.Where(p => !reads.ContainsKey(p)).Order(StringComparer.Ordinal).FirstOrDefault();
        if (unseen is not null)
        {
            refusal = $"the shader binds '{unseen}', but no read of it was found in '{entryPoint}' (read through a function parameter " +
                      "or another form this check does not follow)";
            return null;
        }
        List<string> order = reads.OrderBy(r => r.Value[0]).Select(r => r.Key).ToList();
        for (int a = 0; a < order.Count; a++)
        {
            for (int b = a + 1; b < order.Count; b++)
            {
                if (CanComeFirst(reads[order[b]], reads[order[a]]))
                {
                    refusal = $"whether fxc numbers '{order[a]}' before '{order[b]}' depends on which reads it keeps (one under an if " +
                              "or a switch, in a loop that may not run or in a later loop iteration, or after a return, break or " +
                              "continue that may not run), which this check does not decide";
                    return null;
                }
            }
        }
        return order;

        // Whether some choice of guards keeps a read q of `second` while dropping every read of
        // `first` before q and keeping one of `first` after it: with q's and r's guards kept and
        // every other guard dropped, a read survives only if its guards are all q's or r's.
        // Reads under the same guards are interchangeable here, so each distinct guard set is
        // tried once.
        bool CanComeFirst(List<int> second, List<int> first)
        {
            var before = new List<int[]>();   // the distinct guard sets of `first`'s reads before q
            var beforeKeys = new HashSet<string>(StringComparer.Ordinal);
            var tried = new HashSet<string>(StringComparer.Ordinal);
            int f = 0;
            foreach (int q in second)
            {
                for (; f < first.Count && first[f] < q; f++)
                {
                    int[] g = occurrences[first[f]].Guards;
                    if (g.Length == 0)
                        return false; // certainly kept, before q and every later q
                    if (beforeKeys.Add(GuardKey(g)))
                        before.Add(g);
                }
                // `before` only grows and the reads after q only shrink: a q with the guards of
                // one already tried against the same `before` cannot do better.
                if (!tried.Add(GuardKey(occurrences[q].Guards) + "|" + before.Count))
                    continue;
                var afterKeys = new HashSet<string>(StringComparer.Ordinal);
                for (int k = f; k < first.Count; k++)
                {
                    int[] r = occurrences[first[k]].Guards;
                    if (first[k] < q || !afterKeys.Add(GuardKey(r)))
                        continue;
                    var kept = new HashSet<int>(occurrences[q].Guards);
                    kept.UnionWith(r);
                    if (before.All(o => !o.All(kept.Contains)))
                        return true;
                }
            }
            return false;
        }

        static string GuardKey(int[] guards) => string.Join(',', guards);

        // ---- Walking statements -------------------------------------------------------------

        bool WalkFunction(Fn fn, Guards? path)
        {
            if (!walking.Add(fn.Name))
            {
                why = $"'{fn.Name}' calls itself";
                return false;
            }
            var frame = new Scope { Parent = null, IsFrame = true, IsLoop = false };
            bool ok = WalkList(fn, fn.BodyStart, fn.BodyEnd, new Ctx(path, frame), out _);
            walking.Remove(fn.Name);
            return ok;
        }

        Ctx Guarded(Ctx ctx) => ctx with { Path = new Guards(++nextGuard, ctx.Path) };

        // Every guard over a read here: the path's, and every terminator met so far in an
        // enclosing scope.
        int[] GuardsAt(Ctx ctx)
        {
            var set = new HashSet<int>();
            for (Guards? g = ctx.Path; g is not null; g = g.Next)
                set.Add(g.Id);
            for (Scope? s = ctx.Scope; s is not null; s = s.Parent)
                set.UnionWith(s.Truncations);
            return set.Order().ToArray();
        }

        Guards? PathOf(int[] guards)
        {
            Guards? path = null;
            foreach (int g in guards)
                path = new Guards(g, path);
            return path;
        }

        // A return, break or continue in `ctx` leaves the scope `target`: every later read in it
        // sits under a new guard (the terminator not taken).
        void Truncate(Scope target) => target.Truncations.Add(++nextGuard);

        // The statements in [s, e). `terminated`: an unconditional return, break or continue ended
        // the list; fxc removes what follows it (measured), so the rest is not walked.
        bool WalkList(Fn fn, int s, int e, Ctx ctx, out bool terminated)
        {
            terminated = false;
            for (int k = s; k < e;)
            {
                int last = StatementEnd(k, e);
                if (last < 0)
                    return Fail($"a statement in '{fn.Name}' could not be parsed");
                if (!WalkStatement(fn, k, last, ctx, out bool ended))
                    return false;
                if (ended)
                {
                    terminated = true;
                    return true;
                }
                k = last + 1;
            }
            return true;
        }

        // The statement [k, last].
        bool WalkStatement(Fn fn, int k, int last, Ctx ctx, out bool terminated)
        {
            terminated = false;
            Tok t = code[k];
            if (Is(t, ";"))
                return true;
            if (Is(t, "{"))
                return WalkList(fn, k + 1, match[k], ctx, out terminated);
            if (Is(t, "["))
                return match[k] + 1 <= last && WalkStatement(fn, match[k] + 1, last, ctx, out terminated); // an attribute
            bool paren = k + 1 <= last && Is(code[k + 1], "(");
            switch (t.Kind == Kind.Ident ? t.Text : "")
            {
                case "if" when paren:
                {
                    // Each arm under a guard of its own.
                    int close = match[k + 1];
                    if (!WalkExpression(fn, k + 2, close, ctx))
                        return false;
                    int thenLast = StatementEnd(close + 1, last + 1);
                    if (thenLast < 0 || !WalkStatement(fn, close + 1, thenLast, Guarded(ctx), out _))
                        return Fail($"an 'if' in '{fn.Name}' could not be parsed");
                    if (thenLast == last)
                        return true;
                    if (thenLast + 2 > last || code[thenLast + 1] is not { Kind: Kind.Ident, Text: "else" })
                        return Fail($"an 'if' in '{fn.Name}' could not be parsed");
                    return WalkStatement(fn, thenLast + 2, last, Guarded(ctx), out _);
                }
                case "switch" when paren:
                    return WalkSwitch(fn, k, last, ctx);
                case "for" when paren:
                    return WalkFor(fn, k, last, ctx);
                case "while" when paren:
                {
                    // The condition is tested once; the body may never run.
                    int close = match[k + 1];
                    if (close + 1 > last)
                        return Fail($"a 'while' loop in '{fn.Name}' could not be parsed");
                    var loop = new Scope { Parent = ctx.Scope, IsFrame = false, IsLoop = true };
                    return WalkExpression(fn, k + 2, close, ctx)
                           && WalkStatement(fn, close + 1, last, Guarded(ctx) with { Scope = loop }, out _)
                           && WalkLaterIteration(fn, ctx, loop, testFirst: true, (k + 2, close), (close + 1, last));
                }
                case "do":
                {
                    // The body runs once, then the condition is tested.
                    int bodyLast = StatementEnd(k + 1, last + 1);
                    if (bodyLast < 0 || bodyLast + 2 > last || code[bodyLast + 1] is not { Kind: Kind.Ident, Text: "while" }
                        || !Is(code[bodyLast + 2], "("))
                    {
                        return Fail($"a 'do' loop in '{fn.Name}' could not be parsed");
                    }
                    var loop = new Scope { Parent = ctx.Scope, IsFrame = false, IsLoop = true };
                    Ctx body = ctx with { Scope = loop };
                    return WalkStatement(fn, k + 1, bodyLast, body, out _)
                           && WalkExpression(fn, bodyLast + 3, match[bodyLast + 2], body)
                           && WalkLaterIteration(fn, ctx, loop, testFirst: false, (bodyLast + 3, match[bodyLast + 2]), (k + 1, bodyLast));
                }
                case "return":
                {
                    if (!WalkExpression(fn, k + 1, last, ctx))
                        return false;
                    Scope frame = ctx.Scope;
                    while (!frame.IsFrame)
                        frame = frame.Parent!;
                    Truncate(frame);
                    terminated = true;
                    return true;
                }
                case "break" or "continue":
                {
                    // `break` leaves the innermost loop or switch arm, `continue` the innermost loop.
                    Scope? target = ctx.Scope;
                    while (target is not null && !target.IsFrame && !(target.IsLoop || t.Text == "break"))
                        target = target.Parent;
                    if (target is null || target.IsFrame)
                        return Fail($"a '{t.Text}' in '{fn.Name}' is outside a loop or switch");
                    Truncate(target);
                    terminated = true;
                    return true;
                }
                case "if" or "switch" or "for" or "while" or "else" or "case" or "default":
                    return Fail($"a '{t.Text}' in '{fn.Name}' could not be parsed");
                default:
                    // A declaration or an expression statement. (`discard` is one too: fxc numbers
                    // the reads after it, measured.)
                    return WalkExpression(fn, k, last, ctx);
            }
        }

        // `for (init; cond; step) body`: init and the first test run once; the body only when the
        // loop is certainly entered (FirstIterationRuns), else under a guard.
        bool WalkFor(Fn fn, int k, int last, Ctx ctx)
        {
            int open = k + 1, close = match[open];
            var semis = new List<int>();
            int d = 0;
            for (int j = open + 1; j < close; j++)
            {
                if (code[j].Kind == Kind.Punct && code[j].Text is "(" or "[" or "{") d++;
                else if (code[j].Kind == Kind.Punct && code[j].Text is ")" or "]" or "}") d--;
                else if (d == 0 && Is(code[j], ";")) semis.Add(j);
            }
            if (semis.Count != 2 || close + 1 > last)
                return Fail($"a 'for' loop in '{fn.Name}' could not be parsed");
            if (ReadsAnything(semis[1] + 1, close))
                return Fail("a for loop reads a texture in its increment clause, which runs after the body");
            if (!WalkExpression(fn, open + 1, semis[0], ctx) || !WalkExpression(fn, semis[0] + 1, semis[1], ctx))
                return false;
            var loop = new Scope { Parent = ctx.Scope, IsFrame = false, IsLoop = true };
            Ctx body = (FirstIterationRuns(open + 1, semis[0], semis[1]) ? ctx : Guarded(ctx)) with { Scope = loop };
            return WalkStatement(fn, close + 1, last, body, out _)
                   && WalkLaterIteration(fn, ctx, loop, testFirst: true, (semis[0] + 1, semis[1]), (close + 1, last));
        }

        // A later iteration of a loop: its test and body again, all under a guard of their own
        // (fxc unrolls, and may fold a branch on the counter differently in each copy), and under
        // every break and continue the first iteration met. Its reads come after all of the
        // first iteration's.
        bool WalkLaterIteration(Fn fn, Ctx outer, Scope loop, bool testFirst, (int Start, int End) test, (int Start, int Last) body)
        {
            Ctx again = Guarded(outer) with { Scope = loop };
            return testFirst
                ? WalkExpression(fn, test.Start, test.End, again) && WalkStatement(fn, body.Start, body.Last, again, out _)
                : WalkStatement(fn, body.Start, body.Last, again, out _) && WalkExpression(fn, test.Start, test.End, again);
        }

        // `switch (x) { case …: … default: … }`: every arm under a guard of its own; fxc numbers the
        // arm holding `default` after every other arm, the others in token order (measured).
        bool WalkSwitch(Fn fn, int k, int last, Ctx ctx)
        {
            int close = match[k + 1];
            int open = close + 1;
            if (!WalkExpression(fn, k + 2, close, ctx))
                return false;
            if (open > last || !Is(code[open], "{") || match[open] != last)
                return Fail($"a 'switch' in '{fn.Name}' could not be parsed");

            var arms = new List<(int Start, int End, bool HasDefault)>();
            int j = open + 1;
            while (j < last)
            {
                Tok t = code[j];
                if (t.Kind == Kind.Ident && t.Text is "case" or "default")
                {
                    int colon = j + 1;
                    if (t.Text == "case")
                    {
                        while (colon < last && !Is(code[colon], ":"))
                        {
                            if (Is(code[colon], "?") || Is(code[colon], ";") || Is(code[colon], "{"))
                                return Fail($"a 'case' label in '{fn.Name}' could not be parsed");
                            colon = code[colon].Kind == Kind.Punct && code[colon].Text is "(" or "[" ? match[colon] + 1 : colon + 1;
                        }
                    }
                    if (colon >= last || !Is(code[colon], ":"))
                        return Fail($"a 'switch' label in '{fn.Name}' could not be parsed");
                    if (arms.Count > 0 && arms[^1].Start == j)
                    {
                        arms[^1] = (colon + 1, last, arms[^1].HasDefault || t.Text == "default"); // labels sharing one arm
                    }
                    else
                    {
                        if (arms.Count > 0)
                            arms[^1] = (arms[^1].Start, j, arms[^1].HasDefault);
                        arms.Add((colon + 1, last, t.Text == "default"));
                    }
                    j = colon + 1;
                    continue;
                }
                if (arms.Count == 0)
                    return Fail($"a 'switch' in '{fn.Name}' has code before its first label");
                int end = StatementEnd(j, last);
                if (end < 0)
                    return Fail($"a 'switch' arm in '{fn.Name}' could not be parsed");
                j = end + 1;
            }

            var ended = new bool[arms.Count];
            foreach (int a in Enumerable.Range(0, arms.Count).OrderBy(a => arms[a].HasDefault ? 1 : 0).ThenBy(a => a))
            {
                var arm = new Scope { Parent = ctx.Scope, IsFrame = false, IsLoop = false };
                if (!WalkList(fn, arms[a].Start, arms[a].End, Guarded(ctx) with { Scope = arm }, out ended[a]))
                    return false;
            }
            // fxc rejects a non-empty arm that falls through (X3533); the order of one is not modelled.
            for (int a = 0; a < arms.Count - 1; a++)
            {
                if (!ended[a] && arms[a].Start < arms[a].End)
                    return Fail($"a 'switch' arm in '{fn.Name}' does not end in a break or return");
            }
            return true;
        }

        bool Fail(string reason)
        {
            why ??= reason;
            return false;
        }

        // ---- Walking expressions ------------------------------------------------------------

        // The expression (or declaration) [s, e): reads in evaluation order, a call's arguments
        // before its body.
        bool WalkExpression(Fn fn, int s, int e, Ctx ctx)
        {
            int[] guards = GuardsAt(ctx);
            var pending = new Stack<(int Close, string? Read, Fn? Call)>();
            for (int i = s; i <= e; i++)
            {
                while (pending.Count > 0 && pending.Peek().Close == i)
                {
                    var done = pending.Pop();
                    if (done.Read is { } read)
                    {
                        if (occurrences.Count >= MaxReads)
                            return Fail("the entry point reads its textures too many times to check");
                        occurrences.Add((read, guards));
                    }
                    if (done.Call is { } call && !WalkFunction(call, PathOf(guards)))
                        return false;
                }
                if (i == e)
                    break;
                if (--budget < 0)
                    return Fail("the entry point is too large to walk");

                Tok t = code[i];
                if (t.Kind != Kind.Ident)
                    continue;
                bool afterDot = i > 0 && Is(code[i - 1], ".");
                if (afterDot)
                    continue;
                if (StatementKeywords.Contains(t.Text))
                    return Fail($"a '{t.Text}' inside an expression in '{fn.Name}' could not be parsed");

                if (legacyTextures.Contains(t.Text))
                {
                    if (LocalNames(fn).Contains(t.Text))
                        return Fail($"a parameter or local shadows the legacy texture '{t.Text}'");
                    if (i + 3 < e && Is(code[i + 1], ".") && code[i + 2].Kind == Kind.Ident && Is(code[i + 3], "("))
                    {
                        pending.Push((match[i + 3], t.Text, null));
                        i += 3;
                        continue;
                    }
                    return Fail($"the legacy texture '{t.Text}' is used other than as the object of a method call");
                }

                if (i + 1 < e && Is(code[i + 1], "("))
                {
                    if (LegacyIntrinsic.IsMatch(t.Text))
                    {
                        // A sampler the rewrite did not bind (declared in an #include, compiled
                        // natively) is its own key: its RDEF texture carries the sampler's name.
                        if (i + 2 < e && code[i + 2].Kind == Kind.Ident)
                        {
                            string sampler = code[i + 2].Text;
                            if (LocalNames(fn).Contains(sampler))
                                return Fail($"'{fn.Name}' reads through the sampler parameter or local '{sampler}', whose texture this check does not follow");
                            string texture = legacySamplerTextures.TryGetValue(sampler, out string? bound) ? bound : sampler;
                            pending.Push((match[i + 1], texture, null));
                            i += 1;
                        }
                        continue;
                    }
                    if (functions.TryGetValue(t.Text, out var overloads))
                    {
                        if (overloads.Count != 1)
                            return Fail($"'{t.Text}' is overloaded");
                        pending.Push((match[i + 1], null, new Fn(t.Text, overloads[0].ParamOpen, overloads[0].BodyStart, overloads[0].BodyEnd)));
                        i += 1;
                    }
                }
            }
            return pending.Count == 0 || Fail("a call could not be matched");
        }

        // ---- Certainly-entered loops --------------------------------------------------------

        // `for (int i = A; i OP B; …)`, A an integer literal, B one or a `static const int`
        // initialised from one, OP one of < <= > >= !=: whether the first test holds, in exact
        // integer arithmetic. Any other shape: not certainly entered.
        bool FirstIterationRuns(int initStart, int initEnd, int condEnd)
        {
            int k = initStart;
            if (k + 3 >= initEnd || code[k] is not { Kind: Kind.Ident, Text: "int" } || code[k + 1].Kind != Kind.Ident
                || !Is(code[k + 2], "=") || !IntLiteral(k + 3, initEnd, out long initial))
            {
                return false;
            }
            string counter = code[k + 1].Text;
            int c = initEnd + 1;
            if (c + 2 >= condEnd || code[c].Kind != Kind.Ident || code[c].Text != counter
                || code[c + 1].Kind != Kind.Punct || !CounterTests.Contains(code[c + 1].Text))
            {
                return false;
            }
            long bound;
            if (!IntLiteral(c + 2, condEnd, out bound)
                && !(c + 3 == condEnd && code[c + 2].Kind == Kind.Ident && KnownStaticConstInt(code[c + 2].Text, out bound)))
            {
                return false;
            }
            return code[c + 1].Text switch
            {
                "<" => initial < bound,
                "<=" => initial <= bound,
                ">" => initial > bound,
                ">=" => initial >= bound,
                _ => initial != bound,
            };
        }

        // [s, e) is exactly `[-]N`, N a decimal integer literal an int holds.
        bool IntLiteral(int s, int e, out long value)
        {
            value = 0;
            bool negative = s < e && Is(code[s], "-");
            int k = negative ? s + 1 : s;
            if (k + 1 != e || code[k].Kind != Kind.Number || !IntegerLiteral.IsMatch(code[k].Text))
                return false;
            value = long.Parse(code[k].Text, System.Globalization.CultureInfo.InvariantCulture);
            if (negative) value = -value;
            return value is >= int.MinValue and <= int.MaxValue;
        }

        // The top-level statement [s, e) is exactly `static const int N = [-]literal` (or
        // `const static`): record it; a second declaration of N makes it unknown.
        void StaticConstInt(int s, int e)
        {
            if (e - s < 6 || code[s].Kind != Kind.Ident || code[s + 1].Kind != Kind.Ident)
                return;
            bool qualifiers = (code[s].Text, code[s + 1].Text) is ("static", "const") or ("const", "static");
            if (!qualifiers || code[s + 2] is not { Kind: Kind.Ident, Text: "int" } || code[s + 3].Kind != Kind.Ident
                || !Is(code[s + 4], "=") || !IntLiteral(s + 5, e, out long value))
            {
                return;
            }
            string name = code[s + 3].Text;
            staticConstInts[name] = staticConstInts.ContainsKey(name) ? null : value;
        }

        // A `static const int` the walk can evaluate: declared once, and its name never declared
        // again anywhere (a local, a parameter, a struct member), conservatively.
        bool KnownStaticConstInt(string name, out long value)
        {
            value = 0;
            if (!staticConstInts.TryGetValue(name, out long? known) || known is null)
                return false;
            int declarations = 0;
            for (int j = 1; j < code.Count; j++)
            {
                if (code[j].Kind == Kind.Ident && code[j].Text == name && DeclaringPosition(j))
                    declarations++;
            }
            value = known.Value;
            return declarations == 1;
        }

        // Whether the name at `j` may be declared there: after a type-like identifier, a template's
        // '>' or a ',' (a declarator list), and before what can follow a declarator.
        bool DeclaringPosition(int j)
        {
            if (Is(code[j - 1], "."))
                return false;
            Tok prev = code[j - 1];
            return ((prev.Kind == Kind.Ident && !NotTypes.Contains(prev.Text)) || (Is(prev, ">") && ClosesTemplate(j - 1)) || Is(prev, ","))
                   && j + 1 < code.Count && code[j + 1].Kind == Kind.Punct && code[j + 1].Text is ";" or "=" or "," or "[" or ":" or ")";
        }

        // Whether the '>' at `j` may close a template type (`vector<float, 2>`): a '<' after an
        // identifier earlier in the same statement. `i > N` is a comparison.
        bool ClosesTemplate(int j)
        {
            for (int k = j - 1; k > 0; k--)
            {
                if (code[k].Kind == Kind.Punct && code[k].Text is ";" or "{" or "}")
                    return false;
                if (Is(code[k], "<"))
                    return code[k - 1].Kind == Kind.Ident;
            }
            return false;
        }

        // ---- Names --------------------------------------------------------------------------

        // The parameters and every name declared in the body of `fn` (conservatively: any name in
        // a declaring position, a declarator list's included).
        HashSet<string> LocalNames(Fn fn)
        {
            if (localNames.TryGetValue(fn.Name, out var names))
                return names;
            names = new HashSet<string>(ParamNames(fn.ParamOpen), StringComparer.Ordinal);
            for (int j = fn.BodyStart; j < fn.BodyEnd; j++)
            {
                if (code[j].Kind == Kind.Ident && DeclaringPosition(j))
                    names.Add(code[j].Text);
            }
            localNames[fn.Name] = names;
            return names;
        }

        bool ReadsAnything(int start, int end)
        {
            for (int k = start; k < end; k++)
            {
                if (code[k].Kind == Kind.Ident
                    && (legacyTextures.Contains(code[k].Text) || LegacyIntrinsic.IsMatch(code[k].Text) || functions.ContainsKey(code[k].Text)))
                {
                    return true;
                }
            }
            return false;
        }

        // The last token of the statement starting at `s`.
        int StatementEnd(int s, int limit)
        {
            if (s >= limit) return -1;
            Tok t = code[s];
            if (Is(t, "{")) return match[s] < limit ? match[s] : -1;
            if (Is(t, "[")) return StatementEnd(match[s] + 1, limit);
            if (t.Kind == Kind.Ident && t.Text is "if" && s + 1 < limit && Is(code[s + 1], "("))
            {
                int body = StatementEnd(match[s + 1] + 1, limit);
                if (body < 0) return -1;
                if (body + 1 < limit && code[body + 1].Kind == Kind.Ident && code[body + 1].Text == "else")
                    return StatementEnd(body + 2, limit);
                return body;
            }
            if (t.Kind == Kind.Ident && t.Text is "for" or "while" && s + 1 < limit && Is(code[s + 1], "("))
                return StatementEnd(match[s + 1] + 1, limit);
            if (t.Kind == Kind.Ident && t.Text is "switch" && s + 1 < limit && Is(code[s + 1], "("))
                return match[s + 1] + 1 < limit && Is(code[match[s + 1] + 1], "{") ? match[match[s + 1] + 1] : -1;
            if (t.Kind == Kind.Ident && t.Text is "do")
            {
                int body = StatementEnd(s + 1, limit);
                if (body < 0 || body + 2 >= limit || !Is(code[body + 2], "(")) return -1;
                int after = match[body + 2] + 1;
                return after < limit && Is(code[after], ";") ? after : -1;
            }
            int dd = 0;
            for (int k = s; k < limit; k++)
            {
                Tok u = code[k];
                if (u.Kind != Kind.Punct) continue;
                if (u.Text is "(" or "[" or "{") dd++;
                else if (u.Text is ")" or "]" or "}") dd--;
                else if (dd == 0 && u.Text == ";") return k;
            }
            return -1;
        }

        // The names of the parameters of the function whose parameter list opens at `open`.
        List<string> ParamNames(int open)
        {
            var list = new List<string>();
            int close = match[open];
            int d = 0;
            string? name = null;
            bool done = false;
            for (int k = open + 1; k <= close; k++)
            {
                Tok t = code[k];
                if (k == close || (d == 0 && Is(t, ",")))
                {
                    if (name is not null) list.Add(name);
                    name = null;
                    done = false;
                    continue;
                }
                if (t.Kind == Kind.Punct && t.Text is "(" or "[" or "{") d++;
                else if (t.Kind == Kind.Punct && t.Text is ")" or "]" or "}") d--;
                else if (d == 0 && t.Kind == Kind.Punct && t.Text is ":" or "=") done = true;
                else if (!done && d == 0 && t.Kind == Kind.Ident) name = t.Text;
            }
            return list;
        }
    }

    private static bool Is(Tok t, string punct) => t.Kind == Kind.Punct && t.Text == punct;

    private static readonly string[] TwoCharOps = ["==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "<<", ">>", "&=", "|=", "^="];

    /// <summary>Identifiers, numbers, strings and punctuation; comments and preprocessor lines skipped.</summary>
    private static List<Tok> Tokenize(string s)
    {
        var tokens = new List<Tok>();
        int i = 0;
        bool lineStart = true;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\n') { lineStart = true; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = e < 0 ? s.Length : e + 2;
                continue;
            }
            if (lineStart && c == '#')
            {
                while (i < s.Length && !(s[i] == '\n' && s[i - 1] != '\\')) i++;
                continue;
            }
            lineStart = false;
            if (c == '"')
            {
                int j = i + 1;
                while (j < s.Length && s[j] != '"' && s[j] != '\n') { if (s[j] == '\\') j++; j++; }
                tokens.Add(new Tok(Kind.String, s[i..Math.Min(j + 1, s.Length)]));
                i = j + 1;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int j = i + 1;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                tokens.Add(new Tok(Kind.Ident, s[i..j]));
                i = j;
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int j = i + 1;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '.'
                                        || ((s[j] == '+' || s[j] == '-') && (s[j - 1] == 'e' || s[j - 1] == 'E'))))
                {
                    j++;
                }
                tokens.Add(new Tok(Kind.Number, s[i..j]));
                i = j;
                continue;
            }
            if (i + 1 < s.Length)
            {
                string two = s.Substring(i, 2);
                if (TwoCharOps.Contains(two))
                {
                    tokens.Add(new Tok(Kind.Punct, two));
                    i += 2;
                    continue;
                }
            }
            tokens.Add(new Tok(Kind.Punct, c.ToString()));
            i++;
        }
        return tokens;
    }

    /// <summary>For each bracket the index of its partner; <c>null</c> when unbalanced.</summary>
    private static int[]? MatchBrackets(List<Tok> code)
    {
        var match = new int[code.Count];
        var stack = new Stack<int>();
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].Kind != Kind.Punct) continue;
            string p = code[i].Text;
            if (p is "(" or "{" or "[")
            {
                stack.Push(i);
            }
            else if (p is ")" or "}" or "]")
            {
                if (stack.Count == 0) return null;
                int open = stack.Pop();
                string expected = p == ")" ? "(" : p == "}" ? "{" : "[";
                if (code[open].Text != expected) return null;
                match[open] = i;
                match[i] = open;
            }
        }
        return stack.Count == 0 ? match : null;
    }
}
