#nullable enable

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Preprocessing;

/// <summary>
/// A pure-managed C preprocessor that produces the <b>preprocessed view</b> of an already
/// <c>#include</c>-flattened <c>.fx</c> source: conditional groups evaluated
/// (<c>#if</c>/<c>#ifdef</c>/<c>#ifndef</c>/<c>#elif</c>/<c>#else</c>/<c>#endif</c>) and
/// object-like and function-like macros expanded (with <c>#</c>, <c>##</c> and
/// <c>__VA_ARGS__</c>), honoring <c>#define</c>/<c>#undef</c> in source order.
///
/// <para><b>What it is for.</b> A decision ShadowDusk makes itself (which
/// <c>SamplerState … : register(sN)</c> declarations reserve an OpenGL sampler register,
/// issue #283; which register a legacy sampler pins, issue #299) is made on the same text
/// <c>mgfxc</c> decides it on, the preprocessed one, and identically on every host. The browser's
/// DXC build has no preprocess-only export, so asking DXC would have made the desktop and browser
/// answers differ; this pass is plain C# and runs the same everywhere.</para>
///
/// <para><b>When its output is compiler input, and when it is not.</b> For every effect that
/// compiles from its raw source, DXC still preprocesses and compiles that raw source and this
/// view only informs the decisions above (<see cref="Process(string, string)"/>). The one
/// exception is the legacy-sampler recovery of issue #308
/// (<see cref="ProcessForCompiler"/>): an effect DXC has already REJECTED, whose legacy
/// <c>sampler</c> / <c>tex2D</c> syntax the pre-parser could not rewrite because it lives in an
/// <c>#include</c>d file or comes out of a macro, is pre-parsed again on this preprocessed text
/// and that text is what DXC then compiles. That mode keeps the source's line structure and
/// passes <c>#line</c>, <c>#pragma</c>, <c>#error</c> and <c>#warning</c> through.</para>
///
/// <para><b>Fail loudly.</b> A directive or expression it cannot evaluate is an <c>SD0009</c>
/// error carrying the file and line, never a guess. Callers only surface that error for source
/// DXC itself accepted, so a genuinely malformed shader still reports DXC's own diagnostic.</para>
/// </summary>
internal sealed class FxMacroPreprocessor
{
    /// <summary>The registered diagnostic code for "the preprocessed view could not be built".</summary>
    public const string ErrorCode = "SD0009";

    private const int MaxExpansionSteps = 1_000_000;

    private readonly Dictionary<string, Macro> _macros = new(StringComparer.Ordinal);
    private string _file;
    private int _line;
    private int _expansionSteps;

    // Compiler-input mode (issue #308): the output keeps one line per line of the flattened
    // source, so a token's line in the output IS its line in the input and the flattener's own
    // '#line' directives, passed through verbatim, keep mapping it to the author's file.
    private readonly bool _compilerInput;
    // The (1-based) physical line of the flattened source the output cursor is on.
    private int _cursorLine = 1;
    // The first compiler-predefined macro a conditional tested, which this preprocessor cannot
    // know the value of (DXC defines it; nothing here does).
    private CompilerPredefinedMacroUse? _predefinedUse;

    private FxMacroPreprocessor(string sourceFile, bool compilerInput = false)
    {
        _file = sourceFile;
        _compilerInput = compilerInput;
    }

    /// <summary>
    /// Builds the preprocessed view of <paramref name="flattenedSource"/>, which must already have
    /// its <c>#include</c>s inlined (as <c>ShadowDusk.Core.Preprocessor.Preprocessor.Flatten</c>
    /// leaves it, platform and user <c>#define</c>s prepended).
    /// </summary>
    public static Result<string, ShaderError> Process(string flattenedSource, string sourceFile)
        => new FxMacroPreprocessor(sourceFile).Run(flattenedSource);

    /// <summary>
    /// A conditional (<c>#if</c>, <c>#elif</c>, <c>#ifdef</c>, <c>#ifndef</c>, <c>defined</c>)
    /// that tested a macro only the compiler defines.
    /// </summary>
    internal sealed record CompilerPredefinedMacroUse(string Name, string File, int Line);

    /// <summary>
    /// The preprocessed source in the form that can be handed to the compiler (issue #308), and
    /// whether building it had to assume the value of a compiler-predefined macro.
    /// </summary>
    internal sealed record CompilerInput(string Text, CompilerPredefinedMacroUse? PredefinedMacroUse);

    /// <summary>
    /// Builds the preprocessed view of <paramref name="flattenedSource"/> as COMPILER INPUT: the
    /// same conditional evaluation and macro expansion as <see cref="Process(string, string)"/>,
    /// laid out so the compiler's diagnostics still point at the author's source.
    /// <list type="bullet">
    ///   <item><description>Output line N holds what input line N held: a skipped line, a
    ///   <c>#define</c> or a conditional directive becomes an empty line, never a removed
    ///   one.</description></item>
    ///   <item><description>Active <c>#line</c>, <c>#pragma</c>, <c>#error</c> and
    ///   <c>#warning</c> directives are passed through verbatim on their own line, so the
    ///   compiler maps lines to files exactly as it does for the flattened source and still acts
    ///   on (or fails on) the others.</description></item>
    ///   <item><description>Whitespace is kept and a comment is replaced by spaces of the same
    ///   width, so a line no macro touched keeps its columns.</description></item>
    /// </list>
    ///
    /// <para>A conditional that tests a compiler-predefined macro (<c>__HLSL_VERSION</c>,
    /// <c>__hlsl_dx_compiler</c>, …) is evaluated as if it were undefined, which is what
    /// <c>mgfxc</c>'s own preprocessor does and NOT what DXC does. The use is reported in
    /// <see cref="CompilerInput.PredefinedMacroUse"/> so the caller can refuse to compile a
    /// text that may not be the one DXC would have produced.</para>
    /// </summary>
    internal static Result<CompilerInput, ShaderError> ProcessForCompiler(string flattenedSource, string sourceFile)
    {
        var preprocessor = new FxMacroPreprocessor(sourceFile, compilerInput: true);
        Result<string, ShaderError> view = preprocessor.Run(flattenedSource);
        return view.IsFailure
            ? Result<CompilerInput, ShaderError>.Fail(view.Error)
            : Result<CompilerInput, ShaderError>.Ok(new CompilerInput(view.Value, preprocessor._predefinedUse));
    }

    /// <summary>
    /// The preprocessed view, plus what each of <paramref name="identifiers"/> expands to once the
    /// whole source has been read (the macro table as it stands at the end of the file).
    ///
    /// <para>This is the join between a name read off the RAW source and the same name in the
    /// view (issue #299): the pre-parser knows a sampler by the token the author wrote, which may
    /// itself be a macro (<c>#define SAMP MySampler</c> / <c>sampler SAMP : register(s1);</c>),
    /// while the view only ever shows <c>MySampler</c>. An identifier that is not a macro maps to
    /// itself. End-of-source rather than point-of-declaration is deliberate and sufficient: a
    /// name the shader still uses below its declaration cannot have been <c>#undef</c>'d in
    /// between without the shader failing to compile.</para>
    /// </summary>
    public static Result<(string View, IReadOnlyDictionary<string, string> Resolved), ShaderError> Process(
        string flattenedSource, string sourceFile, IEnumerable<string> identifiers)
    {
        var preprocessor = new FxMacroPreprocessor(sourceFile);
        Result<string, ShaderError> view = preprocessor.Run(flattenedSource);
        if (view.IsFailure)
            return Result<(string, IReadOnlyDictionary<string, string>), ShaderError>.Fail(view.Error);

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string identifier in identifiers)
        {
            var tokens = new List<Tok>();
            Tokenize(identifier, tokens);
            Result<List<Tok>, ShaderError> expanded = preprocessor.Expand(tokens, sourceFile, preprocessor._line);
            if (expanded.IsFailure)
                return Result<(string, IReadOnlyDictionary<string, string>), ShaderError>.Fail(expanded.Error);

            var text = new StringBuilder();
            foreach (Tok t in expanded.Value)
                text.Append(t.Text);
            resolved[identifier] = text.ToString().Trim();
        }

        return Result<(string, IReadOnlyDictionary<string, string>), ShaderError>.Ok((view.Value, resolved));
    }

    // -------------------------------------------------------------------------
    // Line / directive driver
    // -------------------------------------------------------------------------

    private sealed record Macro(string Name, List<string>? Parameters, bool Variadic, List<Tok> Body);

    private sealed class CondFrame
    {
        public bool ParentActive;
        public bool AnyTaken;
        public bool CurrentActive;
        public bool SeenElse;
    }

    private Result<string, ShaderError> Run(string source)
    {
        string text = StripComments(source.Replace("\r\n", "\n").Replace('\r', '\n'), keepWidth: _compilerInput);
        string[] physical = text.Split('\n');

        var output = new StringBuilder();
        var pending = new List<Tok>();
        var conds = new Stack<CondFrame>();
        bool Active() => conds.Count == 0 || conds.Peek().CurrentActive;

        int pendingLine = 0;
        string pendingFile = _file;

        for (int i = 0; i < physical.Length; i++)
        {
            // Splice backslash-newline continuations into one logical line.
            int lineNo = ++_line;
            int physicalLine = i + 1;
            string logical = physical[i];
            while (logical.EndsWith('\\') && i + 1 < physical.Length)
            {
                logical = logical[..^1] + physical[++i];
                _line++;
            }

            string trimmed = logical.TrimStart(' ', '\t', '\f', '\v');
            if (!trimmed.StartsWith('#'))
            {
                if (Active())
                {
                    if (pending.Count == 0)
                    {
                        pendingLine = lineNo;
                        pendingFile = _file;
                    }
                    if (_compilerInput)
                    {
                        Tokenize(logical, pending, physicalLine);
                        // The line break itself is re-created from the line tags when the text is
                        // written out; what is kept here is the token SEPARATION it provides.
                        pending.Add(new Tok(TokKind.Newline, " ") { Line = physicalLine });
                    }
                    else
                    {
                        Tokenize(logical, pending);
                        pending.Add(Tok.Newline);
                    }
                }
                continue;
            }

            // Any directive ends the run of text that macros may span.
            if (pending.Count > 0)
            {
                var flushed = Flush(pending, output, pendingFile, pendingLine);
                if (flushed is { } flushError)
                    return Result<string, ShaderError>.Fail(flushError);
            }

            var dirTokens = new List<Tok>();
            Tokenize(trimmed[1..], dirTokens);
            int at = SkipWs(dirTokens, 0);
            if (at >= dirTokens.Count)
                continue; // null directive '#'

            Tok nameTok = dirTokens[at];
            string directive = nameTok.Kind == TokKind.Number ? "line" : nameTok.Text;
            int restStart = nameTok.Kind == TokKind.Number ? at : at + 1;
            List<Tok> rest = dirTokens.GetRange(restStart, dirTokens.Count - restStart);

            switch (directive)
            {
                case "if":
                case "ifdef":
                case "ifndef":
                {
                    bool parentActive = Active();
                    bool value = false;
                    if (parentActive)
                    {
                        var eval = directive == "if"
                            ? EvaluateCondition(rest, lineNo)
                            : EvaluateDefinedTest(rest, directive == "ifndef", lineNo);
                        if (eval.IsFailure)
                            return Result<string, ShaderError>.Fail(eval.Error);
                        value = eval.Value;
                    }
                    conds.Push(new CondFrame
                    {
                        ParentActive = parentActive,
                        AnyTaken = value,
                        CurrentActive = parentActive && value,
                    });
                    break;
                }
                case "elif":
                {
                    if (conds.Count == 0)
                        return Fail(lineNo, "#elif without a matching #if");
                    CondFrame frame = conds.Peek();
                    if (frame.SeenElse)
                        return Fail(lineNo, "#elif after #else");
                    if (!frame.ParentActive || frame.AnyTaken)
                    {
                        frame.CurrentActive = false;
                        break;
                    }
                    var eval = EvaluateCondition(rest, lineNo);
                    if (eval.IsFailure)
                        return Result<string, ShaderError>.Fail(eval.Error);
                    frame.CurrentActive = eval.Value;
                    frame.AnyTaken = eval.Value;
                    break;
                }
                case "else":
                {
                    if (conds.Count == 0)
                        return Fail(lineNo, "#else without a matching #if");
                    CondFrame frame = conds.Peek();
                    if (frame.SeenElse)
                        return Fail(lineNo, "a second #else in the same conditional group");
                    frame.SeenElse = true;
                    frame.CurrentActive = frame.ParentActive && !frame.AnyTaken;
                    frame.AnyTaken = true;
                    break;
                }
                case "endif":
                {
                    if (conds.Count == 0)
                        return Fail(lineNo, "#endif without a matching #if");
                    conds.Pop();
                    break;
                }
                default:
                {
                    if (!Active())
                        break; // anything goes inside a skipped group

                    switch (directive)
                    {
                        case "define":
                        {
                            var def = ParseDefine(rest, lineNo);
                            if (def.IsFailure)
                                return Result<string, ShaderError>.Fail(def.Error);
                            _macros[def.Value.Name] = def.Value;
                            break;
                        }
                        case "undef":
                        {
                            int n = SkipWs(rest, 0);
                            if (n >= rest.Count || rest[n].Kind != TokKind.Identifier)
                                return Fail(lineNo, "#undef needs a macro name");
                            _macros.Remove(rest[n].Text);
                            break;
                        }
                        case "line":
                        {
                            // The compiler needs the same line-to-file mapping the flattener wrote.
                            if (_compilerInput)
                                EmitDirectiveVerbatim(output, physicalLine, logical);
                            // '#line N "file"' (or the GNU '# N "file"' form): the next line is N.
                            int n = SkipWs(rest, 0);
                            if (n < rest.Count && rest[n].Kind == TokKind.Number &&
                                int.TryParse(rest[n].Text, NumberStyles.None, CultureInfo.InvariantCulture, out int newLine))
                            {
                                _line = newLine - 1;
                                int f = SkipWs(rest, n + 1);
                                if (f < rest.Count && rest[f].Kind == TokKind.String)
                                    _file = Unquote(rest[f].Text);
                            }
                            break;
                        }
                        // Not ours to judge: DXC acts on (or rejects) these itself, and none of
                        // them changes which text is active or what a macro means.
                        case "pragma":
                        case "error":
                        case "warning":
                        case "ident":
                        case "sccs":
                            // As compiler input they have to survive: '#pragma pack_matrix' changes
                            // code generation, and an '#error' must still stop the compile.
                            if (_compilerInput)
                                EmitDirectiveVerbatim(output, physicalLine, logical);
                            break;
                        case "include":
                            return Fail(lineNo,
                                "an #include survived include flattening (a computed '#include MACRO' form?), " +
                                "so the preprocessed view cannot see the file it names");
                        default:
                            return Fail(lineNo, $"unrecognised preprocessor directive '#{directive}'");
                    }
                    break;
                }
            }
        }

        if (conds.Count > 0)
            return Fail(_line, "unterminated conditional group: an #if/#ifdef/#ifndef has no #endif");

        if (pending.Count > 0)
        {
            var flushed = Flush(pending, output, pendingFile, pendingLine);
            if (flushed is { } flushError)
                return Result<string, ShaderError>.Fail(flushError);
        }

        return Result<string, ShaderError>.Ok(output.ToString());
    }

    private ShaderError? Flush(List<Tok> pending, StringBuilder output, string file, int line)
    {
        var expanded = Expand(pending, file, line);
        pending.Clear();
        if (expanded.IsFailure)
            return expanded.Error;
        if (_compilerInput)
        {
            foreach (Tok t in expanded.Value)
            {
                AdvanceCursorTo(output, t.Line);
                // A line's own break is re-created by the next line's tokens. Only one that was
                // carried INTO a later line (a macro argument spanning lines, used after a later
                // one) still has to separate the tokens around it.
                if (t.Kind == TokKind.Newline && t.Line >= _cursorLine)
                    continue;
                output.Append(t.Text);
            }
            return null;
        }
        foreach (Tok t in expanded.Value)
            output.Append(t.Text);
        return null;
    }

    /// <summary>
    /// Compiler-input mode: moves the output cursor down to <paramref name="physicalLine"/> of
    /// the flattened source by writing the missing line breaks. A token tagged with an EARLIER
    /// line (a macro argument the body uses after a later one) stays where the cursor is.
    /// </summary>
    private void AdvanceCursorTo(StringBuilder output, int physicalLine)
    {
        if (physicalLine <= _cursorLine)
            return;
        output.Append('\n', physicalLine - _cursorLine);
        _cursorLine = physicalLine;
    }

    private void EmitDirectiveVerbatim(StringBuilder output, int physicalLine, string directiveLine)
    {
        AdvanceCursorTo(output, physicalLine);
        output.Append(directiveLine.TrimEnd());
    }

    private Result<string, ShaderError> Fail(int line, string message)
        => Result<string, ShaderError>.Fail(Error(_file, line, message));

    private static ShaderError Error(string file, int line, string message)
        => new(File: file, Line: line, Column: 0, Code: ErrorCode,
               Message: "OpenGL sampler-register reservation: could not build the preprocessed view " +
                        $"of this effect ({message}). ShadowDusk decides which 'SamplerState : register(sN)' " +
                        "declarations reserve a GL sampler register on the preprocessed source, like mgfxc, " +
                        "and will not guess when it cannot preprocess it.");

    // -------------------------------------------------------------------------
    // #define parsing
    // -------------------------------------------------------------------------

    private Result<Macro, ShaderError> ParseDefine(List<Tok> rest, int line)
    {
        int n = SkipWs(rest, 0);
        if (n >= rest.Count || rest[n].Kind != TokKind.Identifier)
            return Result<Macro, ShaderError>.Fail(Error(_file, line, "#define needs a macro name"));
        string name = rest[n].Text;
        n++;

        List<string>? parameters = null;
        bool variadic = false;
        // Function-like only when '(' follows the name with NO whitespace between.
        if (n < rest.Count && rest[n].Kind == TokKind.Punct && rest[n].Text == "(")
        {
            parameters = new List<string>();
            n++;
            while (true)
            {
                n = SkipWs(rest, n);
                if (n >= rest.Count)
                    return Result<Macro, ShaderError>.Fail(Error(_file, line, $"unterminated parameter list in #define {name}"));
                Tok p = rest[n];
                if (p.Kind == TokKind.Punct && p.Text == ")" && parameters.Count == 0 && !variadic)
                {
                    n++;
                    break;
                }
                if (p.Kind == TokKind.Punct && p.Text == "...")
                {
                    variadic = true;
                    parameters.Add("__VA_ARGS__");
                }
                else if (p.Kind == TokKind.Identifier)
                {
                    parameters.Add(p.Text);
                }
                else
                {
                    return Result<Macro, ShaderError>.Fail(Error(_file, line, $"malformed parameter list in #define {name}"));
                }
                n = SkipWs(rest, n + 1);
                if (n < rest.Count && rest[n].Kind == TokKind.Punct && rest[n].Text == ",")
                {
                    if (variadic)
                        return Result<Macro, ShaderError>.Fail(Error(_file, line, $"'...' must be the last parameter of #define {name}"));
                    n++;
                    continue;
                }
                if (n < rest.Count && rest[n].Kind == TokKind.Punct && rest[n].Text == ")")
                {
                    n++;
                    break;
                }
                return Result<Macro, ShaderError>.Fail(Error(_file, line, $"malformed parameter list in #define {name}"));
            }
        }

        List<Tok> body = Trim(rest.GetRange(n, rest.Count - n));
        // A '##' written in the body is the paste OPERATOR; mark it so a '##' arriving inside an
        // argument is never mistaken for one.
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i].Kind == TokKind.Punct && body[i].Text == "##")
                body[i] = body[i] with { Kind = TokKind.Paste };
        }
        return Result<Macro, ShaderError>.Ok(new Macro(name, parameters, variadic, body));
    }

    // -------------------------------------------------------------------------
    // Macro expansion (Prosser's hide-set algorithm)
    // -------------------------------------------------------------------------

    private Result<List<Tok>, ShaderError> Expand(List<Tok> input, string file, int line)
    {
        var output = new List<Tok>(input.Count);
        // A stack: the next token to read is at the END.
        var stack = new List<Tok>(input.Count);
        for (int i = input.Count - 1; i >= 0; i--)
            stack.Add(input[i]);

        while (stack.Count > 0)
        {
            if (++_expansionSteps > MaxExpansionSteps)
                return Result<List<Tok>, ShaderError>.Fail(Error(file, line, "macro expansion did not terminate"));

            Tok t = Pop(stack);
            if (t.Kind != TokKind.Identifier ||
                !_macros.TryGetValue(t.Text, out Macro? macro) ||
                t.Hide.Contains(t.Text))
            {
                output.Add(t);
                continue;
            }

            if (macro.Parameters is null)
            {
                var rep = Substitute(macro, null, t.Hide.Add(macro.Name), file, line);
                if (rep.IsFailure)
                    return rep;
                StampLine(rep.Value, t.Line);
                PushAll(stack, rep.Value);
                continue;
            }

            // Function-like: only a call when '(' is the next non-whitespace token.
            int look = stack.Count - 1;
            while (look >= 0 && stack[look].IsWhitespace)
                look--;
            if (look < 0 || stack[look].Kind != TokKind.Punct || stack[look].Text != "(")
            {
                output.Add(t);
                continue;
            }
            stack.RemoveRange(look, stack.Count - look); // whitespace + '('

            var args = new List<List<Tok>> { new() };
            int depth = 0;
            Tok? close = null;
            while (stack.Count > 0)
            {
                Tok a = Pop(stack);
                if (a.Kind == TokKind.Punct && a.Text == "(")
                {
                    depth++;
                }
                else if (a.Kind == TokKind.Punct && a.Text == ")")
                {
                    if (depth == 0)
                    {
                        close = a;
                        break;
                    }
                    depth--;
                }
                else if (a.Kind == TokKind.Punct && a.Text == "," && depth == 0 &&
                         !(macro.Variadic && args.Count == macro.Parameters.Count))
                {
                    args.Add(new List<Tok>());
                    continue;
                }
                args[^1].Add(a);
            }
            if (close is null)
                return Result<List<Tok>, ShaderError>.Fail(Error(file, line, $"unterminated argument list in a call to macro '{macro.Name}'"));

            for (int i = 0; i < args.Count; i++)
                args[i] = Trim(args[i]);

            int expected = macro.Parameters.Count;
            if (expected == 0 && args.Count == 1 && args[0].Count == 0)
                args.Clear();
            else if (macro.Variadic && args.Count == expected - 1)
                args.Add(new List<Tok>()); // empty __VA_ARGS__
            if (args.Count != expected)
                return Result<List<Tok>, ShaderError>.Fail(Error(file, line,
                    $"macro '{macro.Name}' takes {expected} argument(s) but was given {args.Count}"));

            ImmutableHashSet<string> hide = t.Hide.Intersect(close.Hide).Add(macro.Name);
            var replaced = Substitute(macro, args, hide, file, line);
            if (replaced.IsFailure)
                return replaced;
            StampLine(replaced.Value, t.Line);
            PushAll(stack, replaced.Value);
        }

        return Result<List<Tok>, ShaderError>.Ok(output);
    }

    /// <summary>
    /// Compiler-input mode: a token that came out of a macro body (or a paste, or a stringize) has
    /// no source line of its own, so it takes the line of the macro NAME that was invoked. That is
    /// what keeps <c>DECLARE_TEXTURE(S, 1);</c> on the line the author wrote it on. Argument tokens
    /// already carry their own line and keep it.
    /// </summary>
    private void StampLine(List<Tok> replacement, int invocationLine)
    {
        if (!_compilerInput || invocationLine == 0)
            return;
        for (int i = 0; i < replacement.Count; i++)
        {
            if (replacement[i].Line == 0)
                replacement[i] = replacement[i] with { Line = invocationLine };
        }
    }

    private Result<List<Tok>, ShaderError> Substitute(
        Macro macro, List<List<Tok>>? args, ImmutableHashSet<string> hide, string file, int line)
    {
        List<Tok> body = macro.Body;
        var result = new List<Tok>(body.Count);

        int ParamIndex(Tok tok) =>
            args is null || tok.Kind != TokKind.Identifier ? -1 : macro.Parameters!.IndexOf(tok.Text);

        for (int i = 0; i < body.Count; i++)
        {
            Tok b = body[i];

            // '#param' -> string literal (function-like macros only).
            if (args is not null && b.Kind == TokKind.Punct && b.Text == "#")
            {
                int next = SkipWs(body, i + 1);
                if (next < body.Count && ParamIndex(body[next]) is >= 0 and var si)
                {
                    result.Add(new Tok(TokKind.String, Stringize(args[si])));
                    i = next;
                    continue;
                }
                return Result<List<Tok>, ShaderError>.Fail(Error(file, line,
                    $"'#' in macro '{macro.Name}' is not followed by a parameter name"));
            }

            int pi = ParamIndex(b);
            if (pi < 0)
            {
                result.Add(b);
                continue;
            }

            int prev = i - 1;
            while (prev >= 0 && body[prev].IsWhitespace) prev--;
            int after = SkipWs(body, i + 1);
            bool pasted = (prev >= 0 && body[prev].Kind == TokKind.Paste) ||
                          (after < body.Count && body[after].Kind == TokKind.Paste);
            if (pasted)
            {
                if (args![pi].Count == 0)
                    result.Add(Tok.Placemarker);
                else
                    result.AddRange(args[pi]);
            }
            else
            {
                var expandedArg = Expand(args![pi], file, line);
                if (expandedArg.IsFailure)
                    return expandedArg;
                result.AddRange(expandedArg.Value);
            }
        }

        // Token pasting.
        for (int i = 0; i < result.Count; i++)
        {
            if (result[i].Kind != TokKind.Paste)
                continue;
            int l = i - 1;
            while (l >= 0 && result[l].IsWhitespace) l--;
            int r = i + 1;
            while (r < result.Count && result[r].IsWhitespace) r++;
            if (l < 0 || r >= result.Count)
                return Result<List<Tok>, ShaderError>.Fail(Error(file, line,
                    $"'##' at the edge of the body of macro '{macro.Name}'"));

            string joined = result[l].Text + result[r].Text;
            var pastedTokens = new List<Tok>();
            Tokenize(joined, pastedTokens);
            if (pastedTokens.Count == 0)
                pastedTokens.Add(Tok.Placemarker);
            result.RemoveRange(l, r - l + 1);
            result.InsertRange(l, pastedTokens);
            i = l + pastedTokens.Count - 1;
        }

        var final = new List<Tok>(result.Count);
        foreach (Tok t in result)
        {
            if (t.Kind == TokKind.Placemarker)
                continue;
            final.Add(t with { Hide = t.Hide.Union(hide) });
        }
        // Keep the replacement from fusing with its neighbours when re-emitted as text.
        final.Insert(0, Tok.Space);
        final.Add(Tok.Space);
        return Result<List<Tok>, ShaderError>.Ok(final);
    }

    private static string Stringize(List<Tok> arg)
    {
        var sb = new StringBuilder("\"");
        bool pendingSpace = false;
        foreach (Tok t in arg)
        {
            if (t.IsWhitespace)
            {
                pendingSpace = sb.Length > 1;
                continue;
            }
            if (pendingSpace)
                sb.Append(' ');
            pendingSpace = false;
            if (t.Kind is TokKind.String or TokKind.Char)
                sb.Append(t.Text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal));
            else
                sb.Append(t.Text);
        }
        return sb.Append('"').ToString();
    }

    // -------------------------------------------------------------------------
    // #if expression evaluation
    // -------------------------------------------------------------------------

    private Result<bool, ShaderError> EvaluateDefinedTest(List<Tok> rest, bool negate, int line)
    {
        int n = SkipWs(rest, 0);
        if (n >= rest.Count || rest[n].Kind != TokKind.Identifier)
            return Result<bool, ShaderError>.Fail(Error(_file, line, $"#{(negate ? "ifndef" : "ifdef")} needs a macro name"));
        bool defined = _macros.ContainsKey(rest[n].Text);
        NoteCompilerPredefined(rest[n].Text, line);
        return Result<bool, ShaderError>.Ok(negate ? !defined : defined);
    }

    private Result<bool, ShaderError> EvaluateCondition(List<Tok> rest, int line)
    {
        // 'defined X' / 'defined(X)' are resolved BEFORE expansion, so the operand is never expanded.
        var resolved = ResolveDefined(rest, line);
        if (resolved.IsFailure)
            return Result<bool, ShaderError>.Fail(resolved.Error);

        var expanded = Expand(resolved.Value, _file, line);
        if (expanded.IsFailure)
            return Result<bool, ShaderError>.Fail(expanded.Error);

        // A macro that expanded to 'defined' is resolved too (clang does, with a warning).
        var again = ResolveDefined(expanded.Value, line);
        if (again.IsFailure)
            return Result<bool, ShaderError>.Fail(again.Error);

        var tokens = new List<Tok>();
        foreach (Tok t in again.Value)
        {
            if (t.IsWhitespace)
                continue;
            if (t.Kind == TokKind.Identifier)
            {
                // C++ (which DXC's HLSL front end is built on) evaluates true/false; every other
                // identifier left after expansion is 0.
                NoteCompilerPredefined(t.Text, line);
                tokens.Add(new Tok(TokKind.Number, t.Text == "true" ? "1" : "0"));
                continue;
            }
            tokens.Add(t);
        }
        if (tokens.Count == 0)
            return Result<bool, ShaderError>.Fail(Error(_file, line, "#if/#elif with no expression"));

        var evaluator = new ExpressionEvaluator(tokens);
        var value = evaluator.Evaluate();
        if (value.IsFailure)
            return Result<bool, ShaderError>.Fail(Error(_file, line, value.Error));
        return Result<bool, ShaderError>.Ok(value.Value != 0);
    }

    private Result<List<Tok>, ShaderError> ResolveDefined(List<Tok> tokens, int line)
    {
        var result = new List<Tok>(tokens.Count);
        for (int i = 0; i < tokens.Count; i++)
        {
            Tok t = tokens[i];
            if (t.Kind != TokKind.Identifier || t.Text != "defined")
            {
                result.Add(t);
                continue;
            }
            int n = SkipWs(tokens, i + 1);
            bool paren = n < tokens.Count && tokens[n].Kind == TokKind.Punct && tokens[n].Text == "(";
            if (paren)
                n = SkipWs(tokens, n + 1);
            if (n >= tokens.Count || tokens[n].Kind != TokKind.Identifier)
                return Result<List<Tok>, ShaderError>.Fail(Error(_file, line, "'defined' needs a macro name"));
            string name = tokens[n].Text;
            if (paren)
            {
                n = SkipWs(tokens, n + 1);
                if (n >= tokens.Count || tokens[n].Kind != TokKind.Punct || tokens[n].Text != ")")
                    return Result<List<Tok>, ShaderError>.Fail(Error(_file, line, "'defined(' is missing its ')'"));
            }
            NoteCompilerPredefined(name, line);
            result.Add(new Tok(TokKind.Number, _macros.ContainsKey(name) ? "1" : "0"));
            i = n;
        }
        return Result<List<Tok>, ShaderError>.Ok(result);
    }

    /// <summary>
    /// Records the first conditional that tests a macro the COMPILER predefines and the source
    /// does not define itself. This preprocessor evaluates it as undefined (as <c>mgfxc</c>'s
    /// does); DXC would not, so a text built on that assumption may not be the one DXC compiles.
    /// </summary>
    private void NoteCompilerPredefined(string name, int line)
    {
        if (_predefinedUse is null && !_macros.ContainsKey(name) && IsCompilerPredefined(name))
            _predefinedUse = new CompilerPredefinedMacroUse(name, _file, line);
    }

    /// <summary>
    /// The macro names DXC (and the clang front end it is built on) defines without being asked:
    /// <c>__hlsl_dx_compiler</c>, <c>__HLSL_VERSION</c>, <c>__SHADER_TARGET_*</c>,
    /// <c>__DXC_VERSION_*</c>, <c>__spirv__</c> (with <c>-spirv</c>) and the standard
    /// <c>__LINE__</c> / <c>__FILE__</c> family.
    /// </summary>
    internal static bool IsCompilerPredefined(string name) =>
        name.StartsWith("__SHADER_", StringComparison.Ordinal) ||
        name.StartsWith("__HLSL_", StringComparison.Ordinal) ||
        name.StartsWith("__hlsl_", StringComparison.Ordinal) ||
        name.StartsWith("__DXC_", StringComparison.Ordinal) ||
        name.StartsWith("__SPIRV_", StringComparison.Ordinal) ||
        name.StartsWith("__spirv", StringComparison.Ordinal) ||
        name.StartsWith("__clang", StringComparison.Ordinal) ||
        name.StartsWith("__has_", StringComparison.Ordinal) ||
        name is "__LINE__" or "__FILE__" or "__COUNTER__" or "__DATE__" or "__TIME__" or "__TIMESTAMP__"
            or "__INCLUDE_LEVEL__" or "__BASE_FILE__" or "__cplusplus" or "__STDC__"
            or "__STDC_VERSION__" or "__STDC_HOSTED__" or "__VERSION__";

    /// <summary>Integer constant-expression evaluator over the C operator set.</summary>
    private sealed class ExpressionEvaluator
    {
        private readonly List<Tok> _t;
        private int _p;
        private string? _error;

        public ExpressionEvaluator(List<Tok> tokens) => _t = tokens;

        public Result<long, string> Evaluate()
        {
            long v = Ternary(true);
            if (_error is null && _p < _t.Count)
                _error = $"unexpected '{_t[_p].Text}' in #if expression";
            return _error is null ? Result<long, string>.Ok(v) : Result<long, string>.Fail(_error);
        }

        private bool Is(string op) => _p < _t.Count && _t[_p].Kind == TokKind.Punct && _t[_p].Text == op;

        private bool Accept(string op)
        {
            if (!Is(op)) return false;
            _p++;
            return true;
        }

        private long Ternary(bool live)
        {
            long c = Binary(0, live);
            if (!Accept("?")) return c;
            long a = Ternary(live && c != 0);
            if (!Accept(":"))
            {
                _error ??= "'?' without ':' in #if expression";
                return 0;
            }
            long b = Ternary(live && c == 0);
            return c != 0 ? a : b;
        }

        private static readonly string[][] Levels =
        [
            ["||"], ["&&"], ["|"], ["^"], ["&"], ["==", "!="], ["<", ">", "<=", ">="], ["<<", ">>"], ["+", "-"], ["*", "/", "%"],
        ];

        private long Binary(int level, bool live)
        {
            if (level == Levels.Length)
                return Unary(live);
            long left = Binary(level + 1, live);
            while (_error is null)
            {
                string? op = null;
                foreach (string candidate in Levels[level])
                {
                    if (Is(candidate)) { op = candidate; break; }
                }
                if (op is null) break;
                _p++;
                // Short-circuit: the right side of a decided && / || is not evaluated
                // (so '0 && 1/0' is fine, as in C).
                bool rightLive = op switch
                {
                    "&&" => live && left != 0,
                    "||" => live && left == 0,
                    _ => live,
                };
                long right = Binary(level + 1, rightLive);
                left = Apply(op, left, right, rightLive);
            }
            return left;
        }

        private long Apply(string op, long l, long r, bool live)
        {
            switch (op)
            {
                case "||": return (l != 0 || r != 0) ? 1 : 0;
                case "&&": return (l != 0 && r != 0) ? 1 : 0;
                case "|": return l | r;
                case "^": return l ^ r;
                case "&": return l & r;
                case "==": return l == r ? 1 : 0;
                case "!=": return l != r ? 1 : 0;
                case "<": return l < r ? 1 : 0;
                case ">": return l > r ? 1 : 0;
                case "<=": return l <= r ? 1 : 0;
                case ">=": return l >= r ? 1 : 0;
                case "<<": return l << (int)(r & 63);
                case ">>": return l >> (int)(r & 63);
                case "+": return unchecked(l + r);
                case "-": return unchecked(l - r);
                case "*": return unchecked(l * r);
                case "/":
                case "%":
                    if (r == 0)
                    {
                        if (live) _error ??= "division by zero in #if expression";
                        return 0;
                    }
                    if (l == long.MinValue && r == -1) return op == "/" ? long.MinValue : 0;
                    return op == "/" ? l / r : l % r;
                default:
                    _error ??= $"unsupported operator '{op}'";
                    return 0;
            }
        }

        private long Unary(bool live)
        {
            if (Accept("+")) return Unary(live);
            if (Accept("-")) return unchecked(-Unary(live));
            if (Accept("~")) return ~Unary(live);
            if (Accept("!")) return Unary(live) == 0 ? 1 : 0;
            return Primary(live);
        }

        private long Primary(bool live)
        {
            if (_p >= _t.Count)
            {
                _error ??= "#if expression ended early";
                return 0;
            }
            if (Accept("("))
            {
                long v = Ternary(live);
                if (!Accept(")"))
                    _error ??= "missing ')' in #if expression";
                return v;
            }
            Tok t = _t[_p++];
            if (t.Kind == TokKind.Number && TryParseInteger(t.Text, out long n))
                return n;
            if (t.Kind == TokKind.Char && t.Text.Length == 3)
                return t.Text[1];
            _error ??= $"'{t.Text}' is not an integer constant in #if expression";
            return 0;
        }

        private static bool TryParseInteger(string text, out long value)
        {
            string s = text.TrimEnd('u', 'U', 'l', 'L');
            value = 0;
            if (s.Length == 0) return false;
            try
            {
                if (s.Length > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
                    return long.TryParse(s.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
                if (s.Length > 2 && s[0] == '0' && (s[1] == 'b' || s[1] == 'B'))
                {
                    value = Convert.ToInt64(s[2..], 2);
                    return true;
                }
                if (s.Length > 1 && s[0] == '0')
                {
                    value = Convert.ToInt64(s[1..], 8);
                    return true;
                }
            }
            catch (FormatException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
            return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
    }

    // -------------------------------------------------------------------------
    // Lexing
    // -------------------------------------------------------------------------

    internal enum TokKind { Identifier, Number, String, Char, Punct, Whitespace, Newline, Paste, Placemarker }

    internal sealed record Tok(TokKind Kind, string Text)
    {
        public ImmutableHashSet<string> Hide { get; init; } = ImmutableHashSet<string>.Empty;

        /// <summary>
        /// Compiler-input mode only: the (1-based) physical line of the flattened source this
        /// token is written on. 0 everywhere else, and for a token no source line owns yet.
        /// </summary>
        public int Line { get; init; }

        public bool IsWhitespace => Kind is TokKind.Whitespace or TokKind.Newline;

        public static readonly Tok Space = new(TokKind.Whitespace, " ");
        public static readonly Tok Newline = new(TokKind.Newline, "\n");
        public static readonly Tok Placemarker = new(TokKind.Placemarker, "");
    }

    private static readonly string[] Punctuators =
    [
        "...", "<<=", ">>=",
        "##", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||", "++", "--", "->", "::",
        "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=",
    ];

    /// <summary>
    /// Compiler-input mode: tokenizes one source line, keeping each whitespace run as written and
    /// tagging every token with the physical line it sits on.
    /// </summary>
    private static void Tokenize(string text, List<Tok> into, int line)
    {
        int first = into.Count;
        Tokenize(text, into, keepWhitespace: true);
        for (int i = first; i < into.Count; i++)
            into[i] = into[i] with { Line = line };
    }

    private static void Tokenize(string text, List<Tok> into, bool keepWhitespace = false)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            int start = i;
            if (c is ' ' or '\t' or '\f' or '\v' or '\n')
            {
                while (i < text.Length && text[i] is ' ' or '\t' or '\f' or '\v' or '\n') i++;
                into.Add(keepWhitespace ? new Tok(TokKind.Whitespace, text[start..i]) : Tok.Space);
                continue;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_')) i++;
                into.Add(new Tok(TokKind.Identifier, text[start..i]));
                continue;
            }
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                // pp-number: digits, letters, '_', '.', and a sign right after an exponent letter.
                i++;
                while (i < text.Length)
                {
                    char d = text[i];
                    if ((d is '+' or '-') && text[i - 1] is 'e' or 'E' or 'p' or 'P')
                    {
                        i++;
                        continue;
                    }
                    if (char.IsAsciiLetterOrDigit(d) || d is '_' or '.')
                    {
                        i++;
                        continue;
                    }
                    break;
                }
                into.Add(new Tok(TokKind.Number, text[start..i]));
                continue;
            }
            if (c is '"' or '\'')
            {
                i++;
                while (i < text.Length && text[i] != c)
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    i++;
                }
                if (i < text.Length) i++;
                into.Add(new Tok(c == '"' ? TokKind.String : TokKind.Char, text[start..i]));
                continue;
            }
            string? punct = null;
            foreach (string p in Punctuators)
            {
                if (string.CompareOrdinal(text, i, p, 0, p.Length) == 0)
                {
                    punct = p;
                    break;
                }
            }
            punct ??= c.ToString();
            i += punct.Length;
            into.Add(new Tok(TokKind.Punct, punct));
        }
    }

    /// <summary>
    /// Replaces every comment with a space (a block comment keeps its newlines so line numbers
    /// survive), leaving string and character literals intact.
    /// </summary>
    private static string StripComments(string text, bool keepWidth = false)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                // A line comment runs to the newline, but a backslash-newline continues it.
                while (i < text.Length && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        sb.Append('\\').Append('\n');
                        i += 2;
                        continue;
                    }
                    i++;
                }
                sb.Append(' ');
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                // keepWidth (compiler-input mode): the comment becomes spaces of its own width, so
                // the code after it on the same line keeps its column.
                i += 2;
                sb.Append(' ');
                if (keepWidth) sb.Append(' ');
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    if (text[i] == '\n') sb.Append('\n');
                    else if (keepWidth) sb.Append(' ');
                    i++;
                }
                if (keepWidth && i < text.Length)
                    sb.Append(' ', 2);
                i = Math.Min(i + 2, text.Length);
                continue;
            }
            if (c is '"' or '\'')
            {
                // Only a literal that closes on the same line is a literal; an apostrophe in
                // ordinary text (none in valid HLSL, but cheap to be safe) is copied as-is.
                int end = i + 1;
                while (end < text.Length && text[end] != c && text[end] != '\n')
                {
                    if (text[end] == '\\' && end + 1 < text.Length) end++;
                    end++;
                }
                if (end < text.Length && text[end] == c)
                {
                    sb.Append(text, i, end - i + 1);
                    i = end + 1;
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Small helpers
    // -------------------------------------------------------------------------

    private static int SkipWs(List<Tok> tokens, int i)
    {
        while (i < tokens.Count && tokens[i].IsWhitespace) i++;
        return i;
    }

    private static List<Tok> Trim(List<Tok> tokens)
    {
        int s = 0, e = tokens.Count;
        while (s < e && tokens[s].IsWhitespace) s++;
        while (e > s && tokens[e - 1].IsWhitespace) e--;
        return tokens.GetRange(s, e - s);
    }

    private static Tok Pop(List<Tok> stack)
    {
        Tok t = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return t;
    }

    private static void PushAll(List<Tok> stack, List<Tok> tokens)
    {
        for (int i = tokens.Count - 1; i >= 0; i--)
            stack.Add(tokens[i]);
    }

    private static string Unquote(string literal)
        => literal.Length >= 2 ? literal[1..^1].Replace("\\\\", "\\", StringComparison.Ordinal) : literal;
}
