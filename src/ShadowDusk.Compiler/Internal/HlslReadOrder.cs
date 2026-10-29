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
/// function evaluates its arguments, then the function's body. fxc numbers after it has inlined,
/// unrolled loops and folded COMPILE-TIME conditions (an <c>[unroll]</c> loop's
/// <c>if (i == 2)</c> reads in iteration order), but before algebraic dead-code removal (the read
/// in <c>tex2D(B, uv) * 0</c> still counts where it is). So a read inside a region whose condition
/// is a compile-time constant (a literal, a <c>static</c> global, a loop counter, a local or
/// parameter computed only from those) may or may not be the place fxc numbers that texture: when
/// that changes the order against another texture, the walk REFUSES rather than guess. A region
/// whose condition depends on a uniform, a shader input or a texture read is evaluated at run time
/// and keeps token order.</para>
///
/// <para>The walk also refuses: the entry point not found or overloaded, a call to an overloaded
/// function, a legacy texture used other than as the object of a method call, a read in a
/// <c>for</c> loop's increment clause (evaluated after the body), recursion, unbalanced brackets.
/// The caller reports a refusal as <c>SD0230</c> and keeps the backend's slots.</para>
/// </summary>
internal static class HlslReadOrder
{
    private static readonly Regex LegacyIntrinsic = new(@"^tex(1D|2D|3D|CUBE)(bias|grad|lod|proj)?$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Truncating = new(StringComparer.Ordinal) { "return", "break", "continue", "discard" };

    private enum Kind { Ident, Number, Punct, String }

    private readonly record struct Tok(Kind Kind, string Text);

    private sealed class Region
    {
        public required int Start;
        public required int End;
        public required bool Foldable;
        public bool IsLoop;
        public bool Truncates;
    }

    /// <summary>
    /// The legacy texture names in first-read order for <paramref name="entryPoint"/>, or <c>null</c>
    /// with <paramref name="refusal"/> saying why when the order cannot be determined exactly.
    /// </summary>
    /// <param name="preprocessedSource">The compiled HLSL with macros expanded and dead <c>#if</c> arms removed.</param>
    /// <param name="entryPoint">The entry function.</param>
    /// <param name="legacyTextures">The textures legacy samplers sample through (rewritten reads <c>T.Sample(…)</c>).</param>
    /// <param name="legacySamplerTextures">Legacy sampler -> its texture (reads the rewrite did not reach, <c>tex2D(S, …)</c>).</param>
    /// <param name="present">The legacy textures the compiled shader still binds; only their order matters.</param>
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

        // ---- Top level: functions, and which globals are compile-time (static) ----------------
        var functions = new Dictionary<string, List<(int ParamOpen, int BodyStart, int BodyEnd)>>(StringComparer.Ordinal);
        var globals = new Dictionary<string, bool>(StringComparer.Ordinal); // name -> runtime
        int depth = 0;
        int statementStart = 0;
        for (int i = 0; i < code.Count; i++)
        {
            Tok t = code[i];
            if (t.Kind == Kind.Punct && t.Text == "{") { depth++; continue; }
            if (t.Kind == Kind.Punct && t.Text == "}") { depth--; if (depth == 0) statementStart = i + 1; continue; }
            if (t.Kind == Kind.Punct && t.Text == ";" && depth == 0) { statementStart = i + 1; continue; }
            if (t.Kind != Kind.Ident)
                continue;

            if (depth == 0 && i + 1 < code.Count && Is(code[i + 1], "("))
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
                bool isStatic = false;
                for (int k = statementStart; k < i; k++)
                {
                    if (code[k].Kind == Kind.Ident && code[k].Text == "static")
                        isStatic = true;
                }
                // Inside a cbuffer/struct (depth > 0) a member is a uniform/runtime value.
                globals[t.Text] = depth > 0 || !isStatic;
            }
        }

        if (!functions.TryGetValue(entryPoint, out var entry) || entry.Count != 1)
        {
            refusal = $"the entry point '{entryPoint}' was not found exactly once";
            return null;
        }

        var occurrences = new List<(string Texture, bool Foldable, bool InLoop)>();
        var walking = new HashSet<string>(StringComparer.Ordinal);
        string? why = null;

        // Entry parameters are shader inputs: run-time values.
        var entryParams = ParamNames(entry[0].ParamOpen).ToDictionary(p => p.Name, _ => true, StringComparer.Ordinal);
        if (!WalkFunction(entryPoint, entryParams, false, false))
        {
            refusal = why ?? "the entry point's evaluation order is outside the model";
            return null;
        }

        // ---- Order, and the refusal when a compile-time region makes it ambiguous --------------
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int k = 0; k < occurrences.Count; k++)
        {
            if (present.Contains(occurrences[k].Texture))
                first.TryAdd(occurrences[k].Texture, k);
        }
        // An unrolled loop whose body reads under a compile-time condition (a branch on the counter)
        // reads in ITERATION order, which the token order does not give.
        if (first.Count >= 2 && occurrences.Any(o => o.Foldable && o.InLoop && present.Contains(o.Texture)))
        {
            refusal = "a loop reads a texture under a compile-time condition (a branch on the loop counter), so the reads " +
                      "come in iteration order once fxc unrolls it";
            return null;
        }
        foreach ((string texture, int p) in first)
        {
            if (!occurrences[p].Foldable)
                continue;
            // fxc may number this texture at any of its occurrences up to the first unconditional one.
            int last = p;
            for (int k = p + 1; k < occurrences.Count; k++)
            {
                if (!string.Equals(occurrences[k].Texture, texture, StringComparison.Ordinal))
                    continue;
                last = k;
                if (!occurrences[k].Foldable)
                    break;
            }
            foreach ((string other, int q) in first)
            {
                if (!string.Equals(other, texture, StringComparison.Ordinal) && q > p && q <= last)
                {
                    refusal = $"whether fxc numbers '{texture}' before '{other}' depends on a compile-time condition " +
                              "(a branch on a constant or a loop counter) this check does not evaluate";
                    return null;
                }
            }
        }
        return first.OrderBy(f => f.Value).Select(f => f.Key).ToList();

        // ---- Walking --------------------------------------------------------------------------

        bool WalkFunction(string name, Dictionary<string, bool> paramRuntime, bool foldableContext, bool loopContext)
        {
            if (!walking.Add(name))
            {
                why = $"'{name}' calls itself";
                return false;
            }
            var body = functions[name][0];
            var vars = new Dictionary<string, bool>(paramRuntime, StringComparer.Ordinal);
            bool ok = Walk(body.BodyStart, body.BodyEnd, vars, foldableContext, loopContext);
            walking.Remove(name);
            return ok;
        }

        bool IsRuntime(int start, int end, Dictionary<string, bool> vars)
        {
            for (int j = start; j < end; j++)
            {
                Tok t = code[j];
                if (t.Kind != Kind.Ident || (j > 0 && Is(code[j - 1], ".")))
                    continue;
                if (vars.TryGetValue(t.Text, out bool local))
                {
                    if (local) return true;
                    continue;
                }
                if (globals.TryGetValue(t.Text, out bool global) && global)
                    return true;
                if (legacyTextures.Contains(t.Text) || LegacyIntrinsic.IsMatch(t.Text) || functions.ContainsKey(t.Text))
                    return true;
            }
            return false;
        }

        bool Walk(int start, int end, Dictionary<string, bool> vars, bool foldableContext, bool loopContext)
        {
            var regions = new List<Region>();
            bool truncated = false;
            var pending = new Stack<(int Close, string? Read, bool Foldable, bool InLoop, string? Call, Dictionary<string, bool>? CallParams)>();
            int at = start;
            bool InLoop() => loopContext || regions.Any(r => r.IsLoop && r.Start < at);
            bool Foldable() => foldableContext || truncated || regions.Any(r => r.Foldable && !r.IsLoop && r.Start < at);
            bool InRuntimeRegion() => regions.Any(r => !r.Foldable && !r.IsLoop && r.Start < at);

            for (int i = start; i < end; i++)
            {
                at = i;
                while (pending.Count > 0 && pending.Peek().Close == i)
                {
                    var done = pending.Pop();
                    if (done.Read is { } read)
                        occurrences.Add((read, done.Foldable, done.InLoop));
                    if (done.Call is { } call && !WalkFunction(call, done.CallParams!, done.Foldable, done.InLoop))
                        return false;
                }
                for (int r = regions.Count - 1; r >= 0; r--)
                {
                    if (regions[r].End < i)
                    {
                        if (regions[r].Truncates)
                            truncated = true;
                        regions.RemoveAt(r);
                    }
                }

                Tok t = code[i];
                if (t.Kind == Kind.Punct && t.Text == "?")
                {
                    int condStart = ExpressionStart(i);
                    bool runtime = IsRuntime(condStart, i, vars);
                    int colon = MatchingColon(i, end);
                    if (colon < 0)
                    {
                        why = "a conditional expression could not be parsed";
                        return false;
                    }
                    int armEnd = ExpressionEnd(colon + 1, end);
                    regions.Add(new Region { Start = i, End = armEnd, Foldable = !runtime });
                    continue;
                }

                if (t.Kind != Kind.Ident)
                    continue;
                bool afterDot = i > 0 && Is(code[i - 1], ".");

                if (!afterDot && Truncating.Contains(t.Text))
                {
                    foreach (Region r in regions.Where(r => r.Foldable && r.Start < i))
                        r.Truncates = true;
                    continue;
                }

                if (!afterDot && t.Text is "if" or "switch" && i + 1 < end && Is(code[i + 1], "("))
                {
                    int close = match[i + 1];
                    bool runtime = IsRuntime(i + 2, close, vars);
                    int stmtEnd = t.Text == "if" ? StatementEnd(i, end) : (close + 1 < end && Is(code[close + 1], "{") ? match[close + 1] : -1);
                    if (stmtEnd < 0)
                    {
                        why = $"an '{t.Text}' statement could not be parsed";
                        return false;
                    }
                    regions.Add(new Region { Start = close, End = stmtEnd, Foldable = !runtime });
                    continue;
                }

                if (!afterDot && t.Text is "for" or "while" or "do")
                {
                    int loopEnd = StatementEnd(i, end);
                    if (loopEnd > i)
                        regions.Add(new Region { Start = i, End = loopEnd, Foldable = false, IsLoop = true });
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
                    if (semis.Count != 2)
                    {
                        why = "a for loop could not be parsed";
                        return false;
                    }
                    if (ReadsAnything(semis[1] + 1, close))
                    {
                        why = "a for loop reads a texture in its increment clause, which runs after the body";
                        return false;
                    }
                    // The loop counter is a compile-time value once fxc unrolls the loop.
                    for (int k = open + 1; k < semis[0]; k++)
                    {
                        if (code[k].Kind == Kind.Ident && k + 1 < semis[0] && Is(code[k + 1], "="))
                            vars[code[k].Text] = false;
                    }
                    continue;
                }

                if (legacyTextures.Contains(t.Text) && !afterDot)
                {
                    if (vars.ContainsKey(t.Text))
                    {
                        why = $"a parameter or local shadows the legacy texture '{t.Text}'";
                        return false;
                    }
                    if (i + 3 < end && Is(code[i + 1], ".") && code[i + 2].Kind == Kind.Ident && Is(code[i + 3], "("))
                    {
                        pending.Push((match[i + 3], t.Text, Foldable(), InLoop(), null, null));
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
                            string texture = legacySamplerTextures.TryGetValue(code[i + 2].Text, out string? bound)
                                ? bound : code[i + 2].Text;
                            pending.Push((match[i + 1], texture, Foldable(), InLoop(), null, null));
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
                        // A parameter is compile-time inside the callee when its argument is.
                        var args = Arguments(i + 1);
                        var calleeParams = new Dictionary<string, bool>(StringComparer.Ordinal);
                        var names = ParamNames(overloads[0].ParamOpen);
                        for (int p = 0; p < names.Count; p++)
                        {
                            bool runtime = names[p].Out || p >= args.Count || IsRuntime(args[p].Start, args[p].End, vars);
                            calleeParams[names[p].Name] = runtime;
                        }
                        pending.Push((match[i + 1], null, Foldable(), InLoop(), t.Text, calleeParams));
                        i += 1;
                        continue;
                    }
                }

                // An assignment or a declaration with an initializer: track whether the variable is
                // a run-time value. `x = …`, `x += …`, `x.y = …`, `float4 x = …`, `float4 x;`.
                if (!afterDot)
                {
                    int k = i + 1;
                    while (k + 1 < end && Is(code[k], ".") && code[k + 1].Kind == Kind.Ident)
                        k += 2;
                    while (k < end && Is(code[k], "["))
                        k = match[k] + 1;
                    if (k < end && code[k].Kind == Kind.Punct && code[k].Text is "=" or "+=" or "-=" or "*=" or "/=")
                    {
                        int rhsEnd = ExpressionEnd(k + 1, end);
                        // A value assigned under a run-time condition is a run-time value.
                        bool runtime = IsRuntime(k + 1, rhsEnd + 1, vars) || InRuntimeRegion();
                        bool before = vars.TryGetValue(t.Text, out bool b) && b;
                        bool partial = code[k].Text != "=" || k != i + 1; // compound, or one member/element
                        vars[t.Text] = runtime || (partial && before);
                    }
                    else if (k == i + 1 && i > start && (code[i - 1].Kind == Kind.Ident || Is(code[i - 1], ">"))
                             && k < end && code[k].Kind == Kind.Punct && code[k].Text is ";" or ",")
                    {
                        vars[t.Text] = true; // declared without a value: assume run time
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

        // The first token of the expression whose operator is at `at` (a '?'): after the nearest
        // unmatched '(' / '[', ',', '=', 'return', ';', '{', '}' or '?'/':' at the same depth.
        int ExpressionStart(int at)
        {
            int d = 0;
            for (int k = at - 1; k >= 0; k--)
            {
                Tok t = code[k];
                if (t.Kind == Kind.Punct && t.Text is ")" or "]") { d++; continue; }
                if (t.Kind == Kind.Punct && t.Text is "(" or "[") { if (d == 0) return k + 1; d--; continue; }
                if (d != 0) continue;
                if (t.Kind == Kind.Punct && t.Text is "," or ";" or "{" or "}" or "?" or ":" or "=" or "+=" or "-=" or "*=" or "/=")
                    return k + 1;
                if (t.Kind == Kind.Ident && t.Text == "return")
                    return k + 1;
            }
            return 0;
        }

        // The ':' that closes the '?' at `q`.
        int MatchingColon(int q, int limit)
        {
            int d = 0, nested = 0;
            for (int k = q + 1; k < limit; k++)
            {
                Tok t = code[k];
                if (t.Kind != Kind.Punct) continue;
                if (t.Text is "(" or "[" or "{") d++;
                else if (t.Text is ")" or "]" or "}") { if (d == 0) return -1; d--; }
                else if (d == 0 && t.Text == "?") nested++;
                else if (d == 0 && t.Text == ":") { if (nested == 0) return k; nested--; }
                else if (d == 0 && t.Text == ";") return -1;
            }
            return -1;
        }

        // The last token of the expression starting at `from`.
        int ExpressionEnd(int from, int limit)
        {
            int d = 0;
            for (int k = from; k < limit; k++)
            {
                Tok t = code[k];
                if (t.Kind != Kind.Punct) continue;
                if (t.Text is "(" or "[" or "{") d++;
                else if (t.Text is ")" or "]" or "}") { if (d == 0) return k - 1; d--; }
                else if (d == 0 && t.Text is ";" or "," or ":") return k - 1;
            }
            return limit - 1;
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

        // The parameter names of the function whose parameter list opens at `open`, and whether each is out/inout.
        List<(string Name, bool Out)> ParamNames(int open)
        {
            var names = new List<(string, bool)>();
            foreach ((int s, int e) in Arguments(open))
            {
                string? name = null;
                bool isOut = false;
                for (int k = s; k < e; k++)
                {
                    Tok t = code[k];
                    if (t.Kind == Kind.Punct && t.Text is ":" or "=") break;
                    if (t.Kind == Kind.Ident)
                    {
                        if (t.Text is "out" or "inout") isOut = true;
                        name = t.Text;
                    }
                }
                if (name is not null) names.Add((name, isOut));
            }
            return names;
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
