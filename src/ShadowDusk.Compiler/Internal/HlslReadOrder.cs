#nullable enable

using System.Text;
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
/// HLSL is token order with two exceptions handled here: a read <c>T.Sample(S, uv)</c> (or a legacy
/// <c>tex2D(S, uv)</c> the rewrite did not reach) happens after its arguments, and a call to a user
/// function evaluates its arguments, then the function's body. Both arms of a <c>?:</c> are read
/// whatever the condition (measured with a literal <c>false</c>, a <c>static const</c> and
/// <c>0 &gt; 1</c>). fxc numbers after it has inlined, unrolled loops and folded the conditions of
/// <c>if</c>, <c>switch</c> and loops it can evaluate, but before algebraic dead-code removal (the
/// read in <c>tex2D(B, uv) * 0</c> still counts where it is).</para>
///
/// <para><b>Which conditions fxc folds is not modelled.</b> It inlines and folds far beyond any
/// syntactic test (a call returning a constant, a default argument, an <c>out</c> argument, one
/// component of a vector, <c>k &gt; 0 &amp;&amp; false</c>, <c>saturate(-1 - abs(k)) &gt; 0</c>),
/// yet leaves others (<c>k * 0 &gt; 1</c>, <c>k - k &gt; 0</c>), all measured. So EVERY read
/// under an <c>if</c>, a <c>switch</c>, a loop that might run zero times, or after a
/// <c>return</c>/<c>discard</c>/<c>break</c>/<c>continue</c> under one, is CONDITIONAL: fxc may or
/// may not number its texture there. When that could change the order against another texture,
/// the walk REFUSES rather than guess. The one exception is a condition that is CERTAINLY decided
/// at run time: a uniform (a top-level global that is neither <c>static</c> nor <c>const</c>, not
/// shadowed by a local) or an entry-point input the entry never assigns or passes as an
/// <c>out</c> argument, of a signed numeric scalar, one component of such a vector, or a
/// <c>bool</c>; bare, negated, or compared with a short literal or a different such value. fxc
/// cannot fold that (measured: <c>k &gt; 0</c>, <c>uv.x &gt; 0.5</c>, a <c>bool</c> uniform with a
/// <c>false</c> default, a <c>switch</c> on a uniform), so a read under it keeps its place. A
/// <c>for</c> loop whose first iteration certainly runs (literal or <c>static const</c> bounds that
/// hold, or a uniform bound) or a <c>do</c> loop is not conditional either.</para>
///
/// <para>The walk also refuses: the entry point not found or overloaded, a call to an overloaded
/// function, a legacy texture used other than as the object of a method call, a legacy sampler
/// passed through a parameter, a texture the shader binds that the walk never saw read, a read in a
/// <c>for</c> loop's increment clause (evaluated after the body), a conditional read in a loop
/// (fxc reads it in iteration order once it unrolls), recursion, unbalanced brackets. The caller
/// reports a refusal as <c>SD0230</c> and keeps the backend's slots.</para>
/// </summary>
internal static class HlslReadOrder
{
    private static readonly Regex LegacyIntrinsic = new(@"^tex(1D|2D|3D|CUBE)(bias|grad|lod|proj)?$", RegexOptions.CultureInvariant);

    private static readonly Regex ShortLiteral = new(@"^(\d+(\.\d*)?|\.\d+)[fFhH]?$", RegexOptions.CultureInvariant);

    private static readonly Regex OneComponent = new(@"^[xyzwrgba]$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Truncating = new(StringComparer.Ordinal) { "return", "break", "continue", "discard" };

    private static readonly HashSet<string> Relational = new(StringComparer.Ordinal) { "<", ">", "<=", ">=", "==", "!=" };

    private static readonly HashSet<string> Assigning = new(StringComparer.Ordinal) { "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "++", "--" };

    /// <summary>Scalar types a run-time value can be compared in without fxc folding it by range (no <c>uint</c>).</summary>
    private static readonly HashSet<string> NumericScalars = new(StringComparer.Ordinal)
    {
        "float", "half", "double", "int", "min16float", "min10float", "min16int", "min12int",
    };

    /// <summary>Intrinsics and methods that write through an argument.</summary>
    private static readonly HashSet<string> WritesArguments = new(StringComparer.Ordinal) { "modf", "sincos", "frexp", "GetDimensions" };

    /// <summary>Identifiers that can precede a name without declaring it.</summary>
    private static readonly HashSet<string> NotTypes = new(StringComparer.Ordinal) { "return", "else", "case", "do" };

    private enum Kind { Ident, Number, Punct, String }

    private readonly record struct Tok(Kind Kind, string Text);

    private sealed record Param(string Name, string? Type, bool Out, bool Uniform);

    private sealed record Fn(string Name, int ParamOpen, int BodyStart, int BodyEnd, bool IsEntry);

    private sealed class Region
    {
        public required int Start;
        public required int End;
        public required bool Conditional;
        public bool IsLoop;
        public bool IsSwitch;
        /// <summary>Set by a return, discard, break or continue inside: after the region ends, reads up to this token are conditional.</summary>
        public int TruncatesUntil = -1;
    }

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

        // ---- Top level: functions, and the uniform globals (fxc cannot fold their values) --------
        var functions = new Dictionary<string, List<(int ParamOpen, int BodyStart, int BodyEnd)>>(StringComparer.Ordinal);
        var uniformTypes = new Dictionary<string, string>(StringComparer.Ordinal); // name -> type
        var notUniform = new HashSet<string>(StringComparer.Ordinal);
        var constValues = new Dictionary<string, double>(StringComparer.Ordinal); // static const scalar = literal
        int depth = 0;
        int statementStart = 0;
        for (int i = 0; i < code.Count; i++)
        {
            Tok t = code[i];
            if (t.Kind == Kind.Punct && t.Text == "{") { depth++; continue; }
            if (t.Kind == Kind.Punct && t.Text == "}") { depth--; if (depth == 0) statementStart = i + 1; continue; }
            if (t.Kind == Kind.Punct && t.Text == ";" && depth == 0) { statementStart = i + 1; continue; }
            if (t.Kind != Kind.Ident || depth != 0)
                continue; // struct, cbuffer and technique members are not globals the walk can name

            if (i + 1 < code.Count && Is(code[i + 1], "("))
            {
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
                continue;
            }

            // A declared name: preceded by a type (an identifier or a closing template '>'),
            // followed by what can follow a declarator.
            if (i > 0 && (code[i - 1].Kind == Kind.Ident || Is(code[i - 1], ">"))
                && i + 1 < code.Count && code[i + 1].Kind == Kind.Punct
                && code[i + 1].Text is ";" or "=" or ":" or "[" or "," or "<")
            {
                bool isStatic = false, isConst = false;
                for (int k = statementStart; k < i; k++)
                {
                    if (code[k].Kind != Kind.Ident) continue;
                    isStatic |= code[k].Text == "static";
                    isConst |= code[k].Text == "const";
                }
                bool folds = isStatic || isConst;
                string type = code[i - 1].Kind == Kind.Ident ? code[i - 1].Text : ">";
                // `static const int N = 8;`: a loop bound the walk can evaluate.
                int literalEnd = i + 2 < code.Count && Is(code[i + 2], "-") ? i + 4 : i + 3;
                if (isStatic && isConst && NumericScalars.Contains(type) && Is(code[i + 1], "=")
                    && literalEnd < code.Count && Is(code[literalEnd], ";") && Literal(i + 2, literalEnd, out double value)
                    && !constValues.ContainsKey(t.Text))
                {
                    constValues[t.Text] = value;
                }
                else if (constValues.ContainsKey(t.Text))
                {
                    constValues[t.Text] = double.NaN; // declared twice: not a known value
                }
                // Every declaration of the name must be a uniform of one type.
                if (folds || (uniformTypes.TryGetValue(t.Text, out string? seen) && seen != type))
                    notUniform.Add(t.Text);
                else
                    uniformTypes[t.Text] = type;
            }
        }
        foreach (string name in notUniform.Concat(functions.Keys))
            uniformTypes.Remove(name);

        if (!functions.TryGetValue(entryPoint, out var entry) || entry.Count != 1)
        {
            refusal = $"the entry point '{entryPoint}' was not found exactly once";
            return null;
        }

        var occurrences = new List<(string Texture, bool Conditional, bool InLoop)>();
        var walking = new HashSet<string>(StringComparer.Ordinal);
        var localNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string? why = null;

        var entryFn = new Fn(entryPoint, entry[0].ParamOpen, entry[0].BodyStart, entry[0].BodyEnd, IsEntry: true);
        if (!WalkFunction(entryFn, false, false))
        {
            refusal = why ?? "the entry point's evaluation order is outside the model";
            return null;
        }

        // ---- Order, and the refusal when a condition makes it ambiguous -------------------------
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int k = 0; k < occurrences.Count; k++)
        {
            if (present.Contains(occurrences[k].Texture))
                first.TryAdd(occurrences[k].Texture, k);
        }
        string? unseen = present.Where(p => !first.ContainsKey(p)).Order(StringComparer.Ordinal).FirstOrDefault();
        if (unseen is not null)
        {
            refusal = $"the shader binds '{unseen}', but no read of it was found in '{entryPoint}' (read through a function parameter " +
                      "or another form this check does not follow)";
            return null;
        }
        // An unrolled loop whose body reads under a condition (a branch on the counter) reads in
        // ITERATION order, which the token order does not give.
        if (first.Count >= 2 && occurrences.Any(o => o.Conditional && o.InLoop && present.Contains(o.Texture)))
        {
            refusal = "a loop reads a texture under a condition fxc may evaluate (a branch on the loop counter, or a loop that may " +
                      "run zero times), so the reads may come in iteration order once fxc unrolls it";
            return null;
        }
        foreach ((string texture, int p) in first)
        {
            if (!occurrences[p].Conditional)
                continue;
            // fxc may number this texture at any of its occurrences up to the first unconditional one.
            int last = p;
            for (int k = p + 1; k < occurrences.Count; k++)
            {
                if (!string.Equals(occurrences[k].Texture, texture, StringComparison.Ordinal))
                    continue;
                last = k;
                if (!occurrences[k].Conditional)
                    break;
            }
            foreach ((string other, int q) in first)
            {
                if (!string.Equals(other, texture, StringComparison.Ordinal) && q > p && q <= last)
                {
                    refusal = $"whether fxc numbers '{texture}' before '{other}' depends on a condition fxc may evaluate at compile " +
                              "time (its first read is under an if, a switch or a loop, or after a return or discard under one), " +
                              "which this check does not evaluate";
                    return null;
                }
            }
        }
        return first.OrderBy(f => f.Value).Select(f => f.Key).ToList();

        // ---- Walking --------------------------------------------------------------------------

        bool WalkFunction(Fn fn, bool conditionalContext, bool loopContext)
        {
            if (!walking.Add(fn.Name))
            {
                why = $"'{fn.Name}' calls itself";
                return false;
            }
            bool ok = Walk(fn, fn.BodyStart, fn.BodyEnd, conditionalContext, loopContext);
            walking.Remove(fn.Name);
            return ok;
        }

        bool Walk(Fn fn, int start, int end, bool conditionalContext, bool loopContext)
        {
            var regions = new List<Region>();
            int truncatedUntil = -1;
            var pending = new Stack<(int Close, string? Read, bool Conditional, bool InLoop, Fn? Call)>();
            int at = start;
            bool InLoop() => loopContext || regions.Any(r => r.IsLoop && r.Start < at);
            bool Conditional() => conditionalContext || at <= truncatedUntil || regions.Any(r => r.Conditional && r.Start < at);

            for (int i = start; i < end; i++)
            {
                at = i;
                while (pending.Count > 0 && pending.Peek().Close == i)
                {
                    var done = pending.Pop();
                    if (done.Read is { } read)
                        occurrences.Add((read, done.Conditional, done.InLoop));
                    if (done.Call is { } call && !WalkFunction(call, done.Conditional, done.InLoop))
                        return false;
                }
                for (int r = regions.Count - 1; r >= 0; r--)
                {
                    if (regions[r].End < i)
                    {
                        truncatedUntil = Math.Max(truncatedUntil, regions[r].TruncatesUntil);
                        regions.RemoveAt(r);
                    }
                }

                Tok t = code[i];
                if (t.Kind != Kind.Ident)
                    continue;
                bool afterDot = i > 0 && Is(code[i - 1], ".");

                if (!afterDot && Truncating.Contains(t.Text))
                {
                    // A return or discard ends the function, a break its loop or switch, a continue
                    // the iteration. Folded true, the code after the conditional region up to there
                    // is gone; folded false, it stays.
                    Region? scope = t.Text switch
                    {
                        "break" => regions.Where(r => (r.IsLoop || r.IsSwitch) && r.Start < i).MaxBy(r => r.Start),
                        "continue" => regions.Where(r => r.IsLoop && r.Start < i).MaxBy(r => r.Start),
                        _ => null,
                    };
                    int until = scope?.End ?? int.MaxValue;
                    foreach (Region r in regions.Where(r => r.Conditional && r.Start < i && (scope is null || r.Start > scope.Start)))
                        r.TruncatesUntil = Math.Max(r.TruncatesUntil, until);
                    continue;
                }

                if (!afterDot && t.Text is "if" or "switch" && i + 1 < end && Is(code[i + 1], "("))
                {
                    int close = match[i + 1];
                    int stmtEnd = t.Text == "if" ? StatementEnd(i, end) : (close + 1 < end && Is(code[close + 1], "{") ? match[close + 1] : -1);
                    if (stmtEnd < 0)
                    {
                        why = $"an '{t.Text}' statement could not be parsed";
                        return false;
                    }
                    regions.Add(new Region { Start = close, End = stmtEnd, Conditional = !CertainlyRuntime(fn, i + 2, close), IsSwitch = t.Text == "switch" });
                    continue;
                }

                if (!afterDot && t.Text is "while" && i + 1 < end && Is(code[i + 1], "(") && !IsDoWhileTail(i))
                {
                    int loopEnd = StatementEnd(i, end);
                    if (loopEnd < 0)
                    {
                        why = "a while loop could not be parsed";
                        return false;
                    }
                    regions.Add(new Region { Start = i, End = loopEnd, Conditional = !CertainlyRuntime(fn, i + 2, match[i + 1]), IsLoop = true });
                    continue;
                }

                if (!afterDot && t.Text is "do")
                {
                    // The body runs at least once.
                    int loopEnd = StatementEnd(i, end);
                    if (loopEnd < 0)
                    {
                        why = "a do loop could not be parsed";
                        return false;
                    }
                    regions.Add(new Region { Start = i, End = loopEnd, Conditional = false, IsLoop = true });
                    continue;
                }

                if (!afterDot && t.Text == "for" && i + 1 < end && Is(code[i + 1], "("))
                {
                    int open = i + 1, close = match[open];
                    var semis = new List<int>();
                    int d = 0;
                    for (int k = open + 1; k < close; k++)
                    {
                        if (code[k].Kind == Kind.Punct && code[k].Text is "(" or "[" or "{") d++;
                        else if (code[k].Kind == Kind.Punct && code[k].Text is ")" or "]" or "}") d--;
                        else if (d == 0 && Is(code[k], ";")) semis.Add(k);
                    }
                    int loopEnd = StatementEnd(i, end);
                    if (semis.Count != 2 || loopEnd < 0)
                    {
                        why = "a for loop could not be parsed";
                        return false;
                    }
                    if (ReadsAnything(semis[1] + 1, close))
                    {
                        why = "a for loop reads a texture in its increment clause, which runs after the body";
                        return false;
                    }
                    bool runsOnce = FirstIterationRuns(fn, open + 1, semis[0], semis[1]);
                    regions.Add(new Region { Start = i, End = loopEnd, Conditional = !runsOnce, IsLoop = true });
                    continue;
                }

                if (legacyTextures.Contains(t.Text) && !afterDot)
                {
                    if (LocalNames(fn).Contains(t.Text))
                    {
                        why = $"a parameter or local shadows the legacy texture '{t.Text}'";
                        return false;
                    }
                    if (i + 3 < end && Is(code[i + 1], ".") && code[i + 2].Kind == Kind.Ident && Is(code[i + 3], "("))
                    {
                        pending.Push((match[i + 3], t.Text, Conditional(), InLoop(), null));
                        i += 3;
                        continue;
                    }
                    why = $"the legacy texture '{t.Text}' is used other than as the object of a method call";
                    return false;
                }

                if (!afterDot && i + 1 < end && Is(code[i + 1], "("))
                {
                    if (LegacyIntrinsic.IsMatch(t.Text))
                    {
                        // A sampler the rewrite did not bind (declared in an #include, compiled
                        // natively) is its own key: its RDEF texture carries the sampler's name.
                        if (i + 2 < end && code[i + 2].Kind == Kind.Ident)
                        {
                            string sampler = code[i + 2].Text;
                            if (LocalNames(fn).Contains(sampler))
                            {
                                why = $"'{fn.Name}' reads through the sampler parameter or local '{sampler}', whose texture this check does not follow";
                                return false;
                            }
                            string texture = legacySamplerTextures.TryGetValue(sampler, out string? bound) ? bound : sampler;
                            pending.Push((match[i + 1], texture, Conditional(), InLoop(), null));
                            i += 1;
                        }
                        continue;
                    }
                    if (functions.TryGetValue(t.Text, out var overloads))
                    {
                        if (overloads.Count != 1)
                        {
                            why = $"'{t.Text}' is overloaded";
                            return false;
                        }
                        var callee = new Fn(t.Text, overloads[0].ParamOpen, overloads[0].BodyStart, overloads[0].BodyEnd, IsEntry: false);
                        pending.Push((match[i + 1], null, Conditional(), InLoop(), callee));
                        i += 1;
                        continue;
                    }
                }
            }

            while (pending.Count > 0)
            {
                var done = pending.Pop();
                if (done.Close != end)
                {
                    why = "a call could not be matched";
                    return false;
                }
            }
            return true;
        }

        // Whether the 'while' at `w` closes a do-while (its body ends right before it).
        bool IsDoWhileTail(int w) => w > 0 && Is(code[w - 1], "}") && match[w - 1] > 0 && code[match[w - 1] - 1] is { Kind: Kind.Ident, Text: "do" };

        // ---- Run-time conditions ---------------------------------------------------------------

        // Whether the condition in [start, end) is CERTAINLY decided at run time: one run-time
        // value, bare or negated, or a run-time value compared with a short literal or with a
        // different run-time value. Anything else (calls, arithmetic, &&, ||, statics, locals) may
        // be folded by fxc, measured, and is treated as a compile-time condition.
        bool CertainlyRuntime(Fn fn, int start, int end)
        {
            while (end - start >= 2 && Is(code[start], "(") && match[start] == end - 1)
            {
                start++;
                end--;
            }
            if (start >= end)
                return false;
            if (Is(code[start], "!"))
                return CertainlyRuntime(fn, start + 1, end);
            if (!Operand(fn, start, out int lhsEnd, out bool lhsRuntime, out string lhsText, out bool lhsBool))
                return false;
            if (lhsEnd == end)
                return lhsRuntime;
            if (lhsBool || !Relational.Contains(code[lhsEnd].Text) || code[lhsEnd].Kind != Kind.Punct)
                return false;
            if (!Operand(fn, lhsEnd + 1, out int rhsEnd, out bool rhsRuntime, out string rhsText, out bool rhsBool) || rhsEnd != end || rhsBool)
                return false;
            return (lhsRuntime || rhsRuntime) && !string.Equals(lhsText, rhsText, StringComparison.Ordinal);
        }

        // A run-time value or a short numeric literal (optionally negated).
        bool Operand(Fn fn, int s, out int after, out bool runtime, out string text, out bool isBool)
        {
            runtime = false;
            isBool = false;
            text = "";
            after = s;
            int k = s;
            if (k < code.Count && Is(code[k], "-"))
                k++;
            if (k < code.Count && code[k].Kind == Kind.Number && ShortLiteral.IsMatch(code[k].Text))
            {
                after = k + 1;
                text = string.Concat(code.Skip(s).Take(after - s).Select(c => c.Text));
                return true;
            }
            if (k != s || !RuntimeOperand(fn, s, out after, out isBool))
                return false;
            runtime = true;
            text = string.Concat(code.Skip(s).Take(after - s).Select(c => c.Text));
            return true;
        }

        // A uniform global or an unmodified shader input of a numeric scalar, one component of a
        // numeric vector, or a bool.
        bool RuntimeOperand(Fn fn, int s, out int after, out bool isBool)
        {
            after = s;
            isBool = false;
            if (s >= code.Count || code[s].Kind != Kind.Ident || (s > 0 && Is(code[s - 1], ".")))
                return false;
            string name = code[s].Text;
            string? type = null;
            if (fn.IsEntry && ParamsOf(fn.ParamOpen).FirstOrDefault(p => p.Name == name) is { } input)
            {
                if (input.Out || input.Uniform || !InputUnmodified(fn, name))
                    return false; // a `uniform` entry parameter is a compile-time argument of the technique
                type = input.Type;
            }
            else if (!LocalNames(fn).Contains(name) && uniformTypes.TryGetValue(name, out string? global))
            {
                type = global;
            }
            if (type is null)
                return false;

            int k = s + 1;
            bool swizzled = k + 1 < code.Count && Is(code[k], ".") && code[k + 1].Kind == Kind.Ident;
            if (swizzled)
            {
                if (!OneComponent.IsMatch(code[k + 1].Text))
                    return false;
                k += 2;
            }
            after = k;
            if (type == "bool")
            {
                isBool = true;
                return !swizzled;
            }
            if (NumericScalars.Contains(type))
                return true;
            // A vector: one component of it.
            return swizzled && type.Length > 1 && type[^1] is '2' or '3' or '4' && NumericScalars.Contains(type[..^1]);
        }

        // Whether the entry function never assigns, increments or passes out the input `name`, nor
        // declares a local of that name.
        bool InputUnmodified(Fn fn, string name)
        {
            for (int j = fn.BodyStart; j < fn.BodyEnd; j++)
            {
                if (code[j].Kind != Kind.Ident || code[j].Text != name || Is(code[j - 1], "."))
                    continue;
                Tok prev = code[j - 1];
                if ((prev.Kind == Kind.Ident && !NotTypes.Contains(prev.Text)) || Is(prev, ">") || Is(prev, "++") || Is(prev, "--"))
                    return false;
                int k = j + 1;
                while (k < fn.BodyEnd)
                {
                    if (k + 1 < fn.BodyEnd && Is(code[k], ".") && code[k + 1].Kind == Kind.Ident) k += 2;
                    else if (Is(code[k], "[")) k = match[k] + 1;
                    else break;
                }
                if (k < fn.BodyEnd && code[k].Kind == Kind.Punct && Assigning.Contains(code[k].Text))
                    return false;
                // A whole argument: an out/inout parameter of a user function, or an intrinsic that writes it.
                if ((Is(prev, "(") || Is(prev, ",")) && k < fn.BodyEnd && (Is(code[k], ")") || Is(code[k], ",")))
                {
                    int open = EnclosingParen(j);
                    if (open > 0 && code[open - 1].Kind == Kind.Ident)
                    {
                        string callee = code[open - 1].Text;
                        if (WritesArguments.Contains(callee) || callee.StartsWith("Interlocked", StringComparison.Ordinal))
                            return false;
                        if (functions.TryGetValue(callee, out var overloads)
                            && overloads.Any(o => ParamsOf(o.ParamOpen).Any(p => p.Out)))
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        int EnclosingParen(int j)
        {
            int d = 0;
            for (int k = j - 1; k >= 0; k--)
            {
                if (code[k].Kind != Kind.Punct) continue;
                if (code[k].Text is ")" or "]" or "}") d++;
                else if (code[k].Text is "(" or "[" or "{") { if (d == 0) return code[k].Text == "(" ? k : -1; d--; }
            }
            return -1;
        }

        // `for (init; cond; …)`: whether the first iteration certainly runs, or is decided at run
        // time (`int i = 0; i < 3` or `i < Count` with a uniform Count).
        bool FirstIterationRuns(Fn fn, int initStart, int initEnd, int condEnd)
        {
            // init: [type] i = [-]N
            int k = initStart;
            if (k + 1 < initEnd && code[k].Kind == Kind.Ident && code[k + 1].Kind == Kind.Ident)
                k++;
            if (k + 2 >= initEnd || code[k].Kind != Kind.Ident || !Is(code[k + 1], "="))
                return false;
            string counter = code[k].Text;
            if (!Literal(k + 2, initEnd, out double initial))
                return false;
            // cond: i relop X, X a literal or a run-time value.
            int c = initEnd + 1;
            if (c + 2 >= condEnd || code[c].Kind != Kind.Ident || code[c].Text != counter
                || code[c + 1].Kind != Kind.Punct || !Relational.Contains(code[c + 1].Text))
            {
                return false;
            }
            double bound = double.NaN;
            bool known = Literal(c + 2, condEnd, out bound)
                         || (c + 3 == condEnd && code[c + 2].Kind == Kind.Ident && !LocalNames(fn).Contains(code[c + 2].Text)
                             && constValues.TryGetValue(code[c + 2].Text, out bound) && !double.IsNaN(bound));
            if (known)
            {
                return code[c + 1].Text switch
                {
                    "<" => initial < bound,
                    "<=" => initial <= bound,
                    ">" => initial > bound,
                    ">=" => initial >= bound,
                    "!=" => initial != bound,
                    _ => initial == bound,
                };
            }
            return RuntimeOperand(fn, c + 2, out int after, out bool isBool) && after == condEnd && !isBool;
        }

        bool Literal(int s, int e, out double value)
        {
            value = 0;
            bool negative = s < e && Is(code[s], "-");
            int k = negative ? s + 1 : s;
            if (k + 1 != e || code[k].Kind != Kind.Number || !ShortLiteral.IsMatch(code[k].Text))
                return false;
            if (!double.TryParse(code[k].Text.TrimEnd('f', 'F', 'h', 'H'), System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return false;
            }
            if (negative) value = -value;
            return true;
        }

        // The parameters and every name declared in the body of `fn` (conservatively: any name
        // that follows a type-like identifier).
        HashSet<string> LocalNames(Fn fn)
        {
            if (localNames.TryGetValue(fn.Name, out var names))
                return names;
            names = new HashSet<string>(ParamsOf(fn.ParamOpen).Select(p => p.Name), StringComparer.Ordinal);
            for (int j = fn.BodyStart; j < fn.BodyEnd; j++)
            {
                if (code[j].Kind != Kind.Ident || Is(code[j - 1], "."))
                    continue;
                Tok prev = code[j - 1];
                if (((prev.Kind == Kind.Ident && !NotTypes.Contains(prev.Text)) || Is(prev, ">"))
                    && j + 1 < fn.BodyEnd && code[j + 1].Kind == Kind.Punct && code[j + 1].Text is ";" or "=" or "," or "[" or ":" or ")")
                {
                    names.Add(code[j].Text);
                }
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
            if (Is(t, "{")) return match[s];
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

        // The top-level argument ranges [Start, End) of the call whose '(' is at `open`.
        List<(int Start, int End)> Arguments(int open)
        {
            var args = new List<(int, int)>();
            int close = match[open];
            if (close == open + 1) return args;
            int d = 0, s = open + 1;
            for (int k = open + 1; k < close; k++)
            {
                Tok t = code[k];
                if (t.Kind != Kind.Punct) continue;
                if (t.Text is "(" or "[" or "{") d++;
                else if (t.Text is ")" or "]" or "}") d--;
                else if (d == 0 && t.Text == ",") { args.Add((s, k)); s = k + 1; }
            }
            args.Add((s, close));
            return args;
        }

        // The parameters of the function whose parameter list opens at `open`.
        List<Param> ParamsOf(int open)
        {
            var list = new List<Param>();
            foreach ((int s, int e) in Arguments(open))
            {
                string? name = null, type = null;
                bool isOut = false, isUniform = false;
                for (int k = s; k < e; k++)
                {
                    Tok t = code[k];
                    if (t.Kind == Kind.Punct && t.Text is ":" or "=") break;
                    if (t.Kind != Kind.Ident) continue;
                    if (t.Text is "out" or "inout") isOut = true;
                    else if (t.Text is "uniform") isUniform = true;
                    if (name is not null) type = name;
                    name = t.Text;
                }
                if (name is not null) list.Add(new Param(name, type, isOut, isUniform));
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
