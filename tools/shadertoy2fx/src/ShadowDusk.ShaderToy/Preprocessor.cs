using System.Text;

namespace ShadowDusk.ShaderToy;

/// <summary>
/// A C-style preprocessing pass run before lexing. It:
/// <list type="bullet">
/// <item>evaluates conditional compilation
/// (<c>#if</c>/<c>#ifdef</c>/<c>#ifndef</c>/<c>#elif</c>/<c>#else</c>/<c>#endif</c>, correctly
/// nested) with a full integer const-expression evaluator that understands <c>defined(NAME)</c>,
/// the usual C operators, parentheses, and macro expansion — an undefined identifier evaluates to
/// <c>0</c> per the C rule;</item>
/// <item>collects and expands both object-like <c>#define NAME value</c> and function-like
/// <c>#define F(a,b) body</c> macros (argument substitution at call sites), honoring <c>#undef</c>
/// in source order;</item>
/// <item>strips <c>precision</c> statements and the <c>highp</c>/<c>mediump</c>/<c>lowp</c>
/// qualifiers;</item>
/// <item>rejects the unsupported <c>#include</c> directive, and the stringize <c>#</c> / token-paste
/// <c>##</c> operators inside a macro body, with a located diagnostic (never silently dropped).</item>
/// </list>
/// It preserves line counts exactly (directive lines and inactive-branch lines become blank lines)
/// so downstream line/column diagnostics still point at the original source.
/// </summary>
internal sealed class Preprocessor
{
    /// <summary>Defined macros, keyed by name, in current (source-order) state.</summary>
    private readonly Dictionary<string, Macro> _macros = new(StringComparer.Ordinal);

    /// <summary>Guards against runaway recursive macro expansion (a pathological self-referential set).</summary>
    private const int MaxExpansionPasses = 256;

    /// <summary>A defined macro: object-like (no parameters) or function-like (with a parameter list).</summary>
    private sealed record Macro(string Name, IReadOnlyList<string>? Parameters, string Body)
    {
        public bool IsFunctionLike => Parameters is not null;
    }

    /// <summary>State of one conditional nesting level on the <c>#if</c> stack.</summary>
    private sealed class CondFrame
    {
        /// <summary>True once any branch in this group has been taken (so later branches stay off).</summary>
        public bool AnyTaken;

        /// <summary>True if the currently-open branch is active and emitting.</summary>
        public bool CurrentActive;

        /// <summary>True if the enclosing context was active (so a nested group can ever be active).</summary>
        public bool ParentActive;

        /// <summary>True once <c>#else</c> has been seen (a second <c>#else</c>/<c>#elif</c> is an error).</summary>
        public bool SeenElse;
    }

    /// <summary>Run preprocessing, returning text ready for the lexer (line numbers preserved).</summary>
    public string Process(string source)
    {
        // Normalize line endings so the line counter and the lexer agree.
        string normalized = source.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] rawLines = normalized.Split('\n');

        // Join physical lines that end in a backslash into a single logical line (C line-continuation),
        // tracking how many physical lines each logical line spans so we can re-pad blanks and keep the
        // line count exact.
        List<(string Text, int PhysicalCount, int FirstLineNo)> logical = JoinContinuations(rawLines);

        // Accumulate exactly one entry per PHYSICAL source line, then join with '\n'. This preserves
        // the original physical line count (and avoids adding a trailing newline that the source did
        // not have), so downstream line/column diagnostics still line up.
        var physicalOut = new List<string>(rawLines.Length);
        var condStack = new Stack<CondFrame>();

        foreach ((string text, int physicalCount, int firstLineNo) in logical)
        {
            string trimmed = text.TrimStart();
            bool active = condStack.Count == 0 || condStack.Peek().CurrentActive;

            if (trimmed.StartsWith('#'))
            {
                HandleDirective(trimmed, text, firstLineNo, active, condStack);
                EmitBlankPhysicalLines(physicalOut, physicalCount);
                continue;
            }

            if (!active)
            {
                // Inactive branch: drop the content but keep the line count.
                EmitBlankPhysicalLines(physicalOut, physicalCount);
                continue;
            }

            string expanded = ExpandLine(text, firstLineNo);

            // A `precision …;` statement → blank line (keep line count).
            string noPrecisionStmt = StripPrecisionStatement(expanded.TrimStart());
            if (noPrecisionStmt.Length == 0 && expanded.TrimStart().Length != 0)
            {
                EmitBlankPhysicalLines(physicalOut, physicalCount);
                continue;
            }

            // A logical line maps to `physicalCount` physical lines; the (possibly multi-line)
            // expansion goes on the FIRST physical line and the continuation lines stay blank so the
            // physical line count is preserved exactly.
            physicalOut.Add(expanded);
            for (int extra = 1; extra < physicalCount; extra++)
            {
                physicalOut.Add(string.Empty);
            }
        }

        if (condStack.Count > 0)
        {
            throw new ConvertException(
                "Unterminated '#if'/'#ifdef' — missing '#endif'.",
                logical.Count > 0 ? logical[^1].FirstLineNo : 1, 1, "#if");
        }

        string body = string.Join('\n', physicalOut);
        body = StripPrecisionQualifiers(body);
        body = ApplyDeprecatedAliases(body);
        return body;
    }

    private static void EmitBlankPhysicalLines(List<string> output, int count)
    {
        for (int i = 0; i < count; i++)
        {
            output.Add(string.Empty);
        }
    }

    /// <summary>
    /// Fold C backslash-newline line continuations. Returns logical lines, each tagged with the number
    /// of physical lines it consumed and the 1-based line number it started on.
    /// </summary>
    private static List<(string Text, int PhysicalCount, int FirstLineNo)> JoinContinuations(string[] rawLines)
    {
        var result = new List<(string, int, int)>();
        int i = 0;
        while (i < rawLines.Length)
        {
            int firstLineNo = i + 1;
            var sb = new StringBuilder();
            int physical = 0;
            while (true)
            {
                string line = rawLines[i];
                physical++;
                if (line.EndsWith('\\') && i + 1 < rawLines.Length)
                {
                    // Continuation: drop the backslash, splice the next physical line on.
                    sb.Append(line, 0, line.Length - 1);
                    i++;
                    continue;
                }

                sb.Append(line);
                i++;
                break;
            }

            result.Add((sb.ToString(), physical, firstLineNo));
        }

        return result;
    }

    private void HandleDirective(
        string trimmed, string original, int lineNo, bool active,
        Stack<CondFrame> condStack)
    {
        int col = original.IndexOf('#') + 1;
        // Strip C comments from the directive line first: a trailing `// ...` or inline `/* ... */`
        // is not part of a macro body or an #if expression (e.g. `#define X 0 // note`).
        string content = StripComments(trimmed[1..]).TrimStart();
        string keyword = ReadKeyword(content);
        string rest = content[keyword.Length..].Trim();

        switch (keyword)
        {
            case "if":
                PushConditional(EvaluateCondition(rest, lineNo, col), condStack);
                return;
            case "ifdef":
                PushConditional(IsDefinedName(rest, lineNo, col), condStack);
                return;
            case "ifndef":
                PushConditional(!IsDefinedName(rest, lineNo, col), condStack);
                return;
            case "elif":
                HandleElif(rest, lineNo, col, condStack);
                return;
            case "else":
                HandleElse(lineNo, col, condStack);
                return;
            case "endif":
                if (condStack.Count == 0)
                {
                    throw new ConvertException("'#endif' without matching '#if'.", lineNo, col, "#endif");
                }

                condStack.Pop();
                return;
        }

        // Non-conditional directives only take effect in an active region.
        if (!active)
        {
            return;
        }

        switch (keyword)
        {
            case "define":
                ProcessDefine(rest, lineNo, col);
                return;
            case "undef":
                _macros.Remove(rest.Trim());
                return;
            case "version":
            case "extension":
            case "pragma":
            case "line":
            case "":
                return;
            case "include":
                throw new ConvertException(
                    "'#include' is outside the supported subset; inline the included source instead.",
                    lineNo, col, "#include");
            default:
                // G5: glslViewer / Bonzomatic / VShaderEd channel-binding and input metadata directives
                // (`#iChannel0 "tex.png"`, `#iKeyboard`, `#iMouse`, `#iDate`, `#iuniform`, ...) are
                // host-tooling hints, not GLSL. Silently drop them (the host binds those inputs itself)
                // rather than rejecting an otherwise-convertible shader. `#include` stays a loud reject.
                if (IsIgnorableMetadataDirective(keyword))
                {
                    return;
                }

                throw new ConvertException(
                    $"Unsupported preprocessor directive '#{keyword}'.", lineNo, col, "#" + keyword);
        }
    }

    /// <summary>
    /// True for a host-tooling metadata directive that is NOT C-preprocessor syntax and carries no
    /// translatable code: glslViewer / Bonzomatic / VShaderEd channel-binding and input hints. These
    /// are recognized by their leading <c>i</c> (the ShaderToy input-naming convention:
    /// <c>#iChannel0</c>, <c>#iKeyboard</c>, <c>#iMouse</c>, <c>#iDate</c>, <c>#iuniform</c>, …). They
    /// are dropped silently; the host binds those inputs itself. <c>#include</c> is deliberately handled
    /// before this check and stays a loud reject.
    /// </summary>
    private static bool IsIgnorableMetadataDirective(string keyword) =>
        keyword.Length >= 2 && keyword[0] == 'i' && char.IsLetter(keyword[1]);

    private void PushConditional(bool taken, Stack<CondFrame> condStack)
    {
        bool parentActive = condStack.Count == 0 || condStack.Peek().CurrentActive;
        bool active = parentActive && taken;
        condStack.Push(new CondFrame
        {
            AnyTaken = active,
            CurrentActive = active,
            ParentActive = parentActive,
            SeenElse = false,
        });
    }

    private void HandleElif(string rest, int lineNo, int col, Stack<CondFrame> condStack)
    {
        if (condStack.Count == 0)
        {
            throw new ConvertException("'#elif' without matching '#if'.", lineNo, col, "#elif");
        }

        CondFrame frame = condStack.Peek();
        if (frame.SeenElse)
        {
            throw new ConvertException("'#elif' after '#else'.", lineNo, col, "#elif");
        }

        if (!frame.ParentActive || frame.AnyTaken)
        {
            // Either the parent is inactive, or an earlier branch already won: this branch stays off.
            // (Per the C rule, the #elif expression is NOT evaluated once a branch has been taken.)
            frame.CurrentActive = false;
            return;
        }

        bool taken = EvaluateCondition(rest, lineNo, col);
        frame.CurrentActive = taken;
        frame.AnyTaken = taken;
    }

    private static void HandleElse(int lineNo, int col, Stack<CondFrame> condStack)
    {
        if (condStack.Count == 0)
        {
            throw new ConvertException("'#else' without matching '#if'.", lineNo, col, "#else");
        }

        CondFrame frame = condStack.Peek();
        if (frame.SeenElse)
        {
            throw new ConvertException("Duplicate '#else'.", lineNo, col, "#else");
        }

        frame.SeenElse = true;
        frame.CurrentActive = frame.ParentActive && !frame.AnyTaken;
        if (frame.CurrentActive)
        {
            frame.AnyTaken = true;
        }
    }

    /// <summary>
    /// Strip C line (<c>//…</c>) and block (<c>/* … */</c>) comments from a single directive line so
    /// they never leak into a macro body or an <c>#if</c> constant expression. (A block comment is
    /// assumed to be on one line here — directive lines are already continuation-folded.)
    /// </summary>
    private static string StripComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                break;
            }

            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    break; // unterminated on this line — drop the rest
                }

                sb.Append(' ');
                i = end + 2;
                continue;
            }

            sb.Append(s[i]);
            i++;
        }

        return sb.ToString();
    }

    private static string ReadKeyword(string content)
    {
        int n = 0;
        while (n < content.Length && (char.IsLetterOrDigit(content[n]) || content[n] == '_'))
        {
            n++;
        }

        return content[..n];
    }

    private bool IsDefinedName(string rest, int lineNo, int col)
    {
        string name = rest.Trim();
        if (name.Length == 0 || !IsIdentifier(name))
        {
            throw new ConvertException(
                "'#ifdef'/'#ifndef' requires a single macro name.", lineNo, col, name);
        }

        return _macros.ContainsKey(name);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  #define
    // ─────────────────────────────────────────────────────────────────────────

    private void ProcessDefine(string rest, int lineNo, int col)
    {
        if (rest.Length == 0)
        {
            throw new ConvertException("Malformed '#define' (missing macro name).", lineNo, col, "#define");
        }

        int nameEnd = 0;
        while (nameEnd < rest.Length && (char.IsLetterOrDigit(rest[nameEnd]) || rest[nameEnd] == '_'))
        {
            nameEnd++;
        }

        string name = rest[..nameEnd];
        if (name.Length == 0 || char.IsDigit(name[0]))
        {
            throw new ConvertException("Malformed '#define' (invalid macro name).", lineNo, col, "#define");
        }

        // Function-like macro: NAME immediately followed by '(' with NO intervening space.
        if (nameEnd < rest.Length && rest[nameEnd] == '(')
        {
            int close = rest.IndexOf(')', nameEnd);
            if (close < 0)
            {
                throw new ConvertException(
                    $"Malformed function-like '#define {name}(...)' (missing ')').", lineNo, col, "#define");
            }

            string paramList = rest[(nameEnd + 1)..close];
            List<string> parameters = ParseParameterNames(paramList, lineNo, col);
            string body = rest[(close + 1)..].Trim();
            RejectPasteOrStringize(body, name, lineNo, col);
            _macros[name] = new Macro(name, parameters, body);
            return;
        }

        string value = rest[nameEnd..].Trim();
        RejectPasteOrStringize(value, name, lineNo, col);
        _macros[name] = new Macro(name, Parameters: null, value);
    }

    private static List<string> ParseParameterNames(string paramList, int lineNo, int col)
    {
        var parameters = new List<string>();
        string trimmed = paramList.Trim();
        if (trimmed.Length == 0)
        {
            return parameters; // F() — no parameters.
        }

        foreach (string raw in trimmed.Split(','))
        {
            string p = raw.Trim();
            if (p == "...")
            {
                throw new ConvertException(
                    "Variadic macros ('...') are outside the supported subset.", lineNo, col, "...");
            }

            if (!IsIdentifier(p))
            {
                throw new ConvertException(
                    $"Invalid macro parameter name '{p}'.", lineNo, col, p);
            }

            if (parameters.Contains(p))
            {
                throw new ConvertException(
                    $"Duplicate macro parameter name '{p}'.", lineNo, col, p);
            }

            parameters.Add(p);
        }

        return parameters;
    }

    /// <summary>
    /// Token-paste (<c>##</c>) and stringize (<c>#</c>) are rare in shaders and are NOT implemented;
    /// reject a macro body that uses them loudly rather than mis-expand.
    /// </summary>
    private static void RejectPasteOrStringize(string body, string name, int lineNo, int col)
    {
        if (body.Contains("##", StringComparison.Ordinal))
        {
            throw new ConvertException(
                $"Token-paste operator '##' in macro '{name}' is outside the supported subset.",
                lineNo, col, "##");
        }

        // A stringize '#' applies to a parameter inside a function-like body. A bare '#' that is not
        // part of '##' is treated as stringize and rejected.
        if (body.Contains('#'))
        {
            throw new ConvertException(
                $"Stringize operator '#' in macro '{name}' is outside the supported subset.",
                lineNo, col, "#");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Macro expansion (object-like + function-like) on a single source line
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Expand every macro invocation in <paramref name="line"/> using the current macro table.
    /// A bounded pass count guards against pathological recursion.
    /// </summary>
    private string ExpandLine(string line, int lineNo)
    {
        if (_macros.Count == 0)
        {
            return line;
        }

        string current = line;
        for (int pass = 0; pass < MaxExpansionPasses; pass++)
        {
            string next = ExpandOnce(current, lineNo, out bool changed);
            if (!changed)
            {
                return next;
            }

            current = next;
        }

        throw new ConvertException(
            "Macro expansion did not terminate (possible recursive macro).", lineNo, 1, "#define");
    }

    /// <summary>One expansion pass: replace each macro name (and its call arguments) with its body.</summary>
    private string ExpandOnce(string text, int lineNo, out bool changed)
    {
        changed = false;
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            // Skip string/char literals and comments verbatim so we never touch their contents.
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            if (!(char.IsLetter(c) || c == '_'))
            {
                sb.Append(c);
                i++;
                continue;
            }

            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            {
                i++;
            }

            string word = text[start..i];
            if (!_macros.TryGetValue(word, out Macro? macro))
            {
                sb.Append(word);
                continue;
            }

            if (!macro.IsFunctionLike)
            {
                sb.Append(macro.Body);
                changed = true;
                continue;
            }

            // Function-like: only expands when followed (possibly after spaces) by '('.
            int j = i;
            while (j < text.Length && (text[j] == ' ' || text[j] == '\t'))
            {
                j++;
            }

            if (j >= text.Length || text[j] != '(')
            {
                // A function-like macro name not followed by '(' is left as-is (C rule).
                sb.Append(word);
                continue;
            }

            List<string> args = ReadCallArguments(text, j, out int afterCall, lineNo);
            string expansion = SubstituteFunctionMacro(macro, args, lineNo);
            sb.Append(expansion);
            i = afterCall;
            changed = true;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Read a parenthesized, comma-separated argument list starting at the '(' index. Commas inside
    /// nested parentheses do not split arguments. Returns the raw argument strings (trimmed).
    /// </summary>
    private static List<string> ReadCallArguments(string text, int openParen, out int afterCall, int lineNo)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        int i = openParen;
        bool any = false;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(')
            {
                depth++;
                if (depth == 1)
                {
                    any = true;
                    continue; // don't include the outermost '('
                }

                current.Append(c);
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    i++;
                    break;
                }

                current.Append(c);
            }
            else if (c == ',' && depth == 1)
            {
                args.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (depth != 0)
        {
            throw new ConvertException(
                "Unterminated macro argument list ')' expected.", lineNo, 1, "(");
        }

        string last = current.ToString().Trim();
        if (args.Count > 0 || last.Length > 0)
        {
            args.Add(last);
        }
        else if (any)
        {
            // F() with no arguments → an empty argument list.
        }

        afterCall = i;
        return args;
    }

    private string SubstituteFunctionMacro(Macro macro, List<string> args, int lineNo)
    {
        IReadOnlyList<string> ps = macro.Parameters!;

        // Allow F() (zero args) to match a zero-parameter macro.
        if (ps.Count == 0 && args.Count == 1 && args[0].Length == 0)
        {
            args = new List<string>();
        }

        if (args.Count != ps.Count)
        {
            throw new ConvertException(
                $"Macro '{macro.Name}' expects {ps.Count} argument(s) but got {args.Count}.",
                lineNo, 1, macro.Name);
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int k = 0; k < ps.Count; k++)
        {
            map[ps[k]] = args[k];
        }

        // Substitute parameters in the body, replacing whole-word parameter tokens only. Wrap each
        // argument substitution in parentheses so operator precedence at the call site is preserved
        // (the standard hygienic shape, matching the common `#define SQR(x) ((x)*(x))` idiom intent).
        return SubstituteParameters(macro.Body, map);
    }

    private static string SubstituteParameters(string body, IReadOnlyDictionary<string, string> map)
    {
        var sb = new StringBuilder(body.Length);
        int i = 0;
        while (i < body.Length)
        {
            char c = body[i];
            if (c == '/' && i + 1 < body.Length && body[i + 1] == '/')
            {
                sb.Append(body, i, body.Length - i);
                break;
            }

            if (!(char.IsLetter(c) || c == '_'))
            {
                sb.Append(c);
                i++;
                continue;
            }

            int start = i;
            while (i < body.Length && (char.IsLetterOrDigit(body[i]) || body[i] == '_'))
            {
                i++;
            }

            string word = body[start..i];
            if (map.TryGetValue(word, out string? arg))
            {
                // Parenthesize multi-token args so precedence is preserved; leave a bare identifier or
                // number alone to keep the output close to hand-written HLSL.
                sb.Append(NeedsParens(arg) ? "(" + arg + ")" : arg);
            }
            else
            {
                sb.Append(word);
            }
        }

        return sb.ToString();
    }

    /// <summary>Whether an argument needs wrapping parentheses to preserve call-site precedence.</summary>
    private static bool NeedsParens(string arg)
    {
        string t = arg.Trim();
        if (t.Length == 0)
        {
            return false;
        }

        // A single identifier, number, or an already fully-parenthesized expression is safe bare.
        if (IsSimpleAtom(t))
        {
            return false;
        }

        return true;
    }

    private static bool IsSimpleAtom(string t)
    {
        // Identifier or number (incl. dotted member access / swizzle like a.xy and decimals).
        bool allAtom = true;
        foreach (char ch in t)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '.'))
            {
                allAtom = false;
                break;
            }
        }

        return allAtom;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  #if / #elif const-expression evaluation
    // ─────────────────────────────────────────────────────────────────────────

    private bool EvaluateCondition(string expr, int lineNo, int col)
    {
        if (expr.Trim().Length == 0)
        {
            throw new ConvertException("'#if'/'#elif' requires a constant expression.", lineNo, col, "#if");
        }

        // 1. Replace defined(X) / defined X with 1 or 0 BEFORE macro expansion (operand is not expanded).
        string withDefined = ReplaceDefined(expr, lineNo, col);

        // 2. Macro-expand the rest of the expression.
        string expanded = ExpandLine(withDefined, lineNo);

        // 3. Evaluate the integer constant expression.
        var eval = new ExprEvaluator(expanded, lineNo, col);
        long value = eval.Evaluate();
        return value != 0;
    }

    private string ReplaceDefined(string expr, int lineNo, int col)
    {
        var sb = new StringBuilder(expr.Length);
        int i = 0;
        while (i < expr.Length)
        {
            char c = expr[i];
            if ((char.IsLetter(c) || c == '_'))
            {
                int start = i;
                while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '_'))
                {
                    i++;
                }

                string word = expr[start..i];
                if (word == "defined")
                {
                    int j = i;
                    while (j < expr.Length && char.IsWhiteSpace(expr[j]))
                    {
                        j++;
                    }

                    string name;
                    if (j < expr.Length && expr[j] == '(')
                    {
                        j++;
                        int nameStart = j;
                        while (j < expr.Length && char.IsWhiteSpace(expr[j]))
                        {
                            j++;
                            nameStart = j;
                        }

                        int ns = j;
                        while (j < expr.Length && (char.IsLetterOrDigit(expr[j]) || expr[j] == '_'))
                        {
                            j++;
                        }

                        name = expr[ns..j];
                        while (j < expr.Length && char.IsWhiteSpace(expr[j]))
                        {
                            j++;
                        }

                        if (j >= expr.Length || expr[j] != ')')
                        {
                            throw new ConvertException(
                                "Malformed 'defined(NAME)' in '#if'.", lineNo, col, "defined");
                        }

                        j++;
                        _ = nameStart;
                    }
                    else
                    {
                        int ns = j;
                        while (j < expr.Length && (char.IsLetterOrDigit(expr[j]) || expr[j] == '_'))
                        {
                            j++;
                        }

                        name = expr[ns..j];
                    }

                    if (name.Length == 0)
                    {
                        throw new ConvertException(
                            "'defined' requires a macro name.", lineNo, col, "defined");
                    }

                    sb.Append(_macros.ContainsKey(name) ? '1' : '0');
                    i = j;
                    continue;
                }

                sb.Append(word);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Existing pass-through helpers (deprecated aliases, precision stripping)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deprecated ShaderToy uniform aliases. <c>iGlobalTime</c> was the original spelling of
    /// <c>iTime</c> (and <c>iGlobalFrame</c> of <c>iFrame</c>); both still appear in older shaders.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DeprecatedAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["iGlobalTime"] = "iTime",
            ["iGlobalFrame"] = "iFrame",
        };

    private static string ApplyDeprecatedAliases(string body)
    {
        foreach (KeyValuePair<string, string> alias in DeprecatedAliases)
        {
            body = ReplaceWholeWord(body, alias.Key, alias.Value);
        }

        return body;
    }

    /// <summary>If the (trimmed) line is a bare <c>precision …;</c> statement, return empty.</summary>
    private static string StripPrecisionStatement(string trimmed)
    {
        if (trimmed.StartsWith("precision", StringComparison.Ordinal) &&
            (trimmed.Length == 9 || char.IsWhiteSpace(trimmed[9])))
        {
            return string.Empty;
        }

        return trimmed;
    }

    /// <summary>Remove standalone <c>highp</c>/<c>mediump</c>/<c>lowp</c> tokens from the body.</summary>
    private static string StripPrecisionQualifiers(string body)
    {
        foreach (string q in new[] { "highp", "mediump", "lowp" })
        {
            body = ReplaceWholeWord(body, q, string.Empty);
        }

        return body;
    }

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || char.IsDigit(s[0]))
        {
            return false;
        }

        foreach (char c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Replace every whole-word occurrence of <paramref name="word"/> with <paramref name="with"/>,
    /// skipping matches inside identifier boundaries (only token-boundary replacements).
    /// </summary>
    private static string ReplaceWholeWord(string text, string word, string with)
    {
        if (word.Length == 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        int i = 0;
        bool replaced = false;
        while (i < text.Length)
        {
            if (text[i] == word[0] &&
                i + word.Length <= text.Length &&
                string.CompareOrdinal(text, i, word, 0, word.Length) == 0)
            {
                bool leftOk = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
                int after = i + word.Length;
                bool rightOk = after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_');
                if (leftOk && rightOk)
                {
                    sb.Append(with);
                    i = after;
                    replaced = true;
                    continue;
                }
            }

            sb.Append(text[i]);
            i++;
        }

        return replaced ? sb.ToString() : text;
    }

    /// <summary>
    /// A tiny recursive-descent evaluator for the C preprocessor integer constant expression grammar:
    /// the usual operators (<c>! ~ * / % + - &lt;&lt; &gt;&gt; &lt; &lt;= &gt; &gt;= == != &amp; ^ |
    /// &amp;&amp; ||</c>), ternary <c>?:</c>, and parentheses. An identifier that survived macro
    /// expansion (i.e. is an undefined macro) evaluates to 0 per the C rule.
    /// </summary>
    private sealed class ExprEvaluator
    {
        private readonly string _s;
        private readonly int _line;
        private readonly int _col;
        private int _pos;

        public ExprEvaluator(string s, int line, int col)
        {
            _s = s;
            _line = line;
            _col = col;
        }

        public long Evaluate()
        {
            long v = ParseTernary();
            SkipWs();
            if (_pos != _s.Length)
            {
                throw new ConvertException(
                    $"Unexpected token in '#if' expression near '{_s[_pos..]}'.", _line, _col, "#if");
            }

            return v;
        }

        private long ParseTernary()
        {
            long cond = ParseBinary(0);
            SkipWs();
            if (Peek() == '?')
            {
                _pos++;
                long thenV = ParseTernary();
                SkipWs();
                if (Peek() != ':')
                {
                    throw new ConvertException("Expected ':' in '#if' ternary.", _line, _col, "?:");
                }

                _pos++;
                long elseV = ParseTernary();
                return cond != 0 ? thenV : elseV;
            }

            return cond;
        }

        // Precedence-climbing for the binary operators.
        private static readonly (string Op, int Prec)[] BinOps =
        {
            ("||", 1),
            ("&&", 2),
            ("|", 3),
            ("^", 4),
            ("&", 5),
            ("==", 6), ("!=", 6),
            ("<=", 7), (">=", 7), ("<<", 8), (">>", 8), ("<", 7), (">", 7),
            ("+", 9), ("-", 9),
            ("*", 10), ("/", 10), ("%", 10),
        };

        private long ParseBinary(int minPrec)
        {
            long left = ParseUnary();
            while (true)
            {
                SkipWs();
                (string op, int prec) = PeekOperator();
                if (op.Length == 0 || prec < minPrec)
                {
                    return left;
                }

                _pos += op.Length;
                long right = ParseBinary(prec + 1);
                left = Apply(op, left, right);
            }
        }

        private (string, int) PeekOperator()
        {
            SkipWs();
            foreach ((string op, int prec) in BinOps)
            {
                if (Matches(op))
                {
                    // Guard: don't read '<' when the text is '<<' or '<=' etc.; BinOps is ordered so
                    // two-char operators are tried first, so a bare match here is safe.
                    return (op, prec);
                }
            }

            return (string.Empty, 0);
        }

        private bool Matches(string op)
        {
            if (_pos + op.Length > _s.Length)
            {
                return false;
            }

            if (string.CompareOrdinal(_s, _pos, op, 0, op.Length) != 0)
            {
                return false;
            }

            // For a one-char operator that is the prefix of a two-char operator, ensure we are not at
            // the start of the longer form (e.g. don't match '<' inside '<<' or '<=').
            if (op.Length == 1)
            {
                char next = _pos + 1 < _s.Length ? _s[_pos + 1] : '\0';
                char c = op[0];
                if (c == '<' && (next == '<' || next == '=')) { return false; }
                if (c == '>' && (next == '>' || next == '=')) { return false; }
                if (c == '&' && next == '&') { return false; }
                if (c == '|' && next == '|') { return false; }
                if (c == '=' && next == '=') { return false; }
            }

            return true;
        }

        private long ParseUnary()
        {
            SkipWs();
            char c = Peek();
            if (c == '!')
            {
                _pos++;
                return ParseUnary() == 0 ? 1 : 0;
            }

            if (c == '~')
            {
                _pos++;
                return ~ParseUnary();
            }

            if (c == '-')
            {
                _pos++;
                return -ParseUnary();
            }

            if (c == '+')
            {
                _pos++;
                return ParseUnary();
            }

            return ParsePrimary();
        }

        private long ParsePrimary()
        {
            SkipWs();
            char c = Peek();
            if (c == '(')
            {
                _pos++;
                long v = ParseTernary();
                SkipWs();
                if (Peek() != ')')
                {
                    throw new ConvertException("Expected ')' in '#if' expression.", _line, _col, "#if");
                }

                _pos++;
                return v;
            }

            if (char.IsDigit(c))
            {
                return ParseNumber();
            }

            if (char.IsLetter(c) || c == '_')
            {
                // An identifier that reached the evaluator is an undefined macro → 0 (C rule).
                // (true/false are honored as 1/0 for GLSL-flavored conditions.)
                int start = _pos;
                while (_pos < _s.Length && (char.IsLetterOrDigit(_s[_pos]) || _s[_pos] == '_'))
                {
                    _pos++;
                }

                string word = _s[start.._pos];
                return word switch
                {
                    "true" => 1,
                    "false" => 0,
                    _ => 0,
                };
            }

            throw new ConvertException(
                $"Unexpected character '{c}' in '#if' expression.", _line, _col, "#if");
        }

        private long ParseNumber()
        {
            int start = _pos;
            if (_s[_pos] == '0' && _pos + 1 < _s.Length && (_s[_pos + 1] is 'x' or 'X'))
            {
                _pos += 2;
                int hexStart = _pos;
                while (_pos < _s.Length && Uri.IsHexDigit(_s[_pos]))
                {
                    _pos++;
                }

                long hex = Convert.ToInt64(_s[hexStart.._pos], 16);
                SkipIntegerSuffix();
                return hex;
            }

            while (_pos < _s.Length && char.IsDigit(_s[_pos]))
            {
                _pos++;
            }

            // A floating literal is not valid in a preprocessor constant expression; reject it.
            if (_pos < _s.Length && _s[_pos] == '.')
            {
                throw new ConvertException(
                    "Floating-point literals are not allowed in a '#if' expression.", _line, _col, "#if");
            }

            long value = long.Parse(_s[start.._pos]);
            SkipIntegerSuffix();
            return value;
        }

        private void SkipIntegerSuffix()
        {
            while (_pos < _s.Length && (_s[_pos] is 'u' or 'U' or 'l' or 'L'))
            {
                _pos++;
            }
        }

        private static long Apply(string op, long a, long b) => op switch
        {
            "||" => (a != 0 || b != 0) ? 1 : 0,
            "&&" => (a != 0 && b != 0) ? 1 : 0,
            "|" => a | b,
            "^" => a ^ b,
            "&" => a & b,
            "==" => a == b ? 1 : 0,
            "!=" => a != b ? 1 : 0,
            "<=" => a <= b ? 1 : 0,
            ">=" => a >= b ? 1 : 0,
            "<<" => a << (int)b,
            ">>" => a >> (int)b,
            "<" => a < b ? 1 : 0,
            ">" => a > b ? 1 : 0,
            "+" => a + b,
            "-" => a - b,
            "*" => a * b,
            "/" => b == 0 ? throw new ConvertException("Division by zero in '#if'.", 0, 0, "/") : a / b,
            "%" => b == 0 ? throw new ConvertException("Modulo by zero in '#if'.", 0, 0, "%") : a % b,
            _ => throw new InvalidOperationException($"Unhandled operator '{op}'."),
        };

        private char Peek() => _pos < _s.Length ? _s[_pos] : '\0';

        private void SkipWs()
        {
            while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos]))
            {
                _pos++;
            }
        }
    }
}
