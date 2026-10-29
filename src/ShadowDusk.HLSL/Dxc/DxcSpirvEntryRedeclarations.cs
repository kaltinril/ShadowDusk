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
/// <para><b>Why removing the prototype is faithful.</b> A prototype adds nothing the definition
/// does not carry: DXC's own DXIL path takes the entry's semantics from the definition and
/// never from a prototype, and fxc (<c>mgfxc</c> 3.8.4.1) compiles these shapes. So a prototype
/// whose return type and parameter types match the single definition is blanked from the
/// compile's input: replaced by spaces, keeping every line break, so no diagnostic line or
/// column moves. DXC then sees one declaration and compiles the definition it would have
/// compiled. An overload prototype (different parameters, no definition of its own) of an
/// entry function nothing calls is inert, fxc compiles past it, and it is blanked too.</para>
///
/// <para><b>Decided on the preprocessed text, located in the raw text.</b> DXC compiles the text
/// after its preprocessor, so every decision is made on the managed preprocessed view
/// (<see cref="FxMacroPreprocessor.ProcessForCompiler"/>: conditionals evaluated, macros
/// expanded, comments replaced by spaces, one output line per input line): which declarations of
/// the entry exist, which are prototypes, and where each starts (its return type, with only
/// storage modifiers and attributes before it) and ends. The raw text is used only to LOCATE each
/// prototype to blank, at the same line and column, and the raw range is accepted only when it
/// reads token for token as the preprocessed one (comments aside, and conditional directives
/// wholly inside it, whose inactive lines the view left empty). A prototype that a macro writes,
/// renames or moves fails that match and is refused rather than guessed at.</para>
///
/// <para><b>Fail-safe.</b> The crash kills the consumer's process, so no possibly-redeclared
/// entry reaches the SPIR-V back end. Two or more declaration-shaped uses of the entry name at
/// global scope, any of which cannot be read as a plain declaration, are refused with
/// <c>SD0236</c>. So is a call to the entry function between its prototype and its definition
/// (without the prototype the call has no declaration; fxc compiles it), an overload prototype
/// of an entry function the source calls, a prototype whose return type is spelled differently
/// from the definition's in a way that cannot be told apart from DXC's own type identity, and
/// (when the preprocessed view cannot be built) any prototype a raw-text reading cannot pin
/// down. No definition or several, and a prototype whose return type is a different BUILT-IN
/// type, reach DXC, whose front end reports them cleanly (<c>missing entry point
/// definition</c>, <c>ambiguous entry point function</c>, <c>functions that differ only in their
/// return type</c>) before the back end runs.</para>
///
/// <para>The validated DXIL of DirectX 12 and the DXBC of DirectX 11 and FNA never had the
/// fault and keep their input as is (see <see cref="AppliesTo"/>). A source with no prototype
/// of the entry point is returned unchanged, so every shader that compiled before compiles to
/// the same bytes.</para>
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
        if (string.IsNullOrEmpty(entryPoint) || CannotBeDeclaredTwice(source, entryPoint))
            return Ok(source);

        // `-D` macros come before the text, on lines of their own that are dropped from the view.
        string prefix = string.Concat((macros ?? []).Select(m => $"#define {m.Name} {m.Value ?? "1"}\n"));
        Result<FxMacroPreprocessor.CompilerInput, ShaderError> view = FxMacroPreprocessor.ProcessForCompiler(prefix + source, file);
        if (view.IsSuccess && view.Value.PredefinedMacroUse is null)
        {
            string text = DropLines(view.Value.Text, prefix.Count(c => c == '\n'));
            return FromView(source, text, entryPoint, file);
        }

        string why = view.IsFailure
            ? view.Error.Message
            : $"a conditional tests '{view.Value.PredefinedMacroUse!.Name}', a macro only the compiler defines";
        return FromRawText(source, entryPoint, file, why);
    }

    // ---- The decision on the preprocessed view --------------------------------------------

    private static Result<string, ShaderError> FromView(string source, string text, string entryPoint, string file)
    {
        Analysis analysis = Analyze(text, entryPoint, rawText: false);
        if (analysis.DeclarationShaped < 2)
            return Ok(source);
        if (analysis.Unread.Count > 0)
            return Fail(Unread(text, file, entryPoint, analysis.Unread[0]));

        Result<IReadOnlyList<Declaration>, ShaderError> plan = Plan(analysis, text, file, entryPoint);
        if (plan.IsFailure)
            return Fail(plan.Error);
        if (plan.Value.Count == 0)
            return Ok(source);

        var located = new LineIndex(source);
        var expanded = new LineIndex(text);
        var spans = new List<(int Start, int End)>();
        foreach (Declaration prototype in plan.Value)
        {
            (int Start, int End)? span = located.LineCount == expanded.LineCount
                ? Locate(source, located, text, expanded, prototype)
                : null;
            if (span is null)
            {
                return Fail(Refuse(text, file, prototype.NameLine, prototype.NameColumn,
                    $"entry point '{entryPoint}' is declared more than once, and this declaration cannot be matched to the " +
                    "source as written: a macro writes, renames or moves part of it. " + CrashSentence + " A declaration " +
                    $"a macro produces cannot be removed from the source, so write the prototype of '{entryPoint}' out " +
                    "in full on lines of its own, or remove it."));
            }
            spans.Add(span.Value);
        }
        return Ok(Blank(source, spans));
    }

    /// <summary>
    /// The raw range that holds <paramref name="prototype"/> (read off the preprocessed
    /// <paramref name="text"/>), or null when the raw text there is not that prototype token for
    /// token.
    /// </summary>
    private static (int Start, int End)? Locate(string source, LineIndex raw, string text, LineIndex expanded, Declaration prototype)
    {
        (int startLine, int startColumn) = expanded.Position(prototype.Start);
        (int endLine, int endColumn) = expanded.Position(prototype.End);
        if (raw.Offset(startLine, startColumn) is not int start || raw.Offset(endLine, endColumn) is not int end || end <= start)
            return null;

        // The same first character at a token boundary, and the same `;` last.
        if (source[start] != text[prototype.Start] || source[end - 1] != ';'
            || (start > 0 && IsIdentifierChar(source[start - 1])))
        {
            return null;
        }

        List<Tok> expectedTokens = Tokenize(text[prototype.Start..prototype.End], rawText: false)
            .Where(t => t.Kind != TokKind.Directive).ToList();

        // Raw tokens: comments dropped; conditional directives must open and close inside the
        // range (blanking them with it then removes exactly the prototype); a line the view left
        // empty is an inactive branch (or a directive) and holds nothing the compile sees.
        var rawTokens = new List<Tok>();
        int conditionalDepth = 0;
        foreach (Tok t in Tokenize(source[start..end], rawText: true))
        {
            if (t.Kind == TokKind.Comment)
                continue;
            int line = raw.Position(start + t.Offset).Line;
            if (t.Kind == TokKind.Directive)
            {
                string keyword = DirectiveKeyword(t.Text);
                if (keyword is "if" or "ifdef" or "ifndef")
                    conditionalDepth++;
                else if (keyword is "elif" or "else")
                {
                    if (conditionalDepth == 0)
                        return null;
                }
                else if (keyword == "endif")
                {
                    if (--conditionalDepth < 0)
                        return null;
                }
                else
                {
                    return null; // #define, #undef, #line, #pragma, #include, ... inside a declaration
                }
                continue;
            }
            if (expanded.IsBlankLine(text, line))
                continue;
            rawTokens.Add(t);
        }
        if (conditionalDepth != 0 || rawTokens.Count != expectedTokens.Count)
            return null;
        for (int i = 0; i < rawTokens.Count; i++)
        {
            if (rawTokens[i].Kind != expectedTokens[i].Kind
                || !string.Equals(rawTokens[i].Text, expectedTokens[i].Text, StringComparison.Ordinal))
            {
                return null;
            }
        }
        return (start, end);
    }

    // ---- Without a view: the raw text alone, refusing whatever it cannot pin down ----------

    private static Result<string, ShaderError> FromRawText(string source, string entryPoint, string file, string why)
    {
        Analysis analysis = Analyze(source, entryPoint, rawText: true);

        // A macro that names the entry or pastes tokens can write a declaration this reading
        // cannot see; together with any declaration-shaped use that may be a second one.
        if (analysis.MacroCouldDeclare && analysis.DeclarationShaped > 0)
        {
            Token at = analysis.Unread.Count > 0 ? analysis.Unread[0]
                : analysis.Prototypes.Concat(analysis.Definitions).Select(d => new Token(d.NameLine, d.NameColumn)).First();
            return Fail(Refuse(source, file, at.Line, at.Column,
                $"entry point '{entryPoint}' may be declared more than once through a macro, and the source cannot be " +
                $"preprocessed here to tell ({why}). {CrashSentence} Write the declarations of '{entryPoint}' out without " +
                "a macro, or remove its prototypes."));
        }
        if (analysis.DeclarationShaped < 2)
            return Ok(source);
        if (analysis.Unread.Count > 0)
            return Fail(Unread(source, file, entryPoint, analysis.Unread[0]));
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
        return Ok(plan.Value.Count > 0 ? Blank(source, plan.Value.Select(d => (d.Start, d.End)).ToList()) : source);
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

    private const string CrashSentence =
        "DXC's SPIR-V back end (the OpenGL and Vulkan targets, and the raylib and SkSL converters) crashes the process " +
        "on an entry point that is declared more than once, so ShadowDusk removes the entry point's prototypes before " +
        "compiling.";

    private static ShaderError Unread(string text, string file, string entryPoint, Token at) =>
        Refuse(text, file, at.Line, at.Column,
            $"entry point '{entryPoint}' is named here at global scope in a form ShadowDusk cannot read as a declaration, " +
            $"beside another declaration of it. {CrashSentence} Whether this is a second declaration cannot be told safely, " +
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

    /// <summary>Replaces every character of each range with a space, keeping line breaks.</summary>
    private static string Blank(string source, IReadOnlyList<(int Start, int End)> spans)
    {
        var builder = new StringBuilder(source);
        foreach ((int start, int end) in spans)
        {
            for (int i = start; i < end; i++)
            {
                if (builder[i] is not ('\r' or '\n'))
                    builder[i] = ' ';
            }
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
    private sealed record Declaration(int Start, int End, int NameLine, int NameColumn, string ReturnKey, string ParameterKey);

    /// <summary>A position in the analyzed text.</summary>
    private sealed record Token(int Line, int Column, int Offset = 0);

    /// <param name="Prototypes">Global-scope declarations of the entry without a body.</param>
    /// <param name="Definitions">Global-scope declarations of the entry with a body.</param>
    /// <param name="Calls">Uses of the entry name inside a body (calls), technique references excluded.</param>
    /// <param name="Unread">Global-scope uses of the name that may be declarations but do not read as one.</param>
    /// <param name="MacroCouldDeclare">Raw text only: a directive names the entry or pastes tokens.</param>
    private sealed record Analysis(
        IReadOnlyList<Declaration> Prototypes,
        IReadOnlyList<Declaration> Definitions,
        IReadOnlyList<Token> Calls,
        IReadOnlyList<Token> Unread,
        bool MacroCouldDeclare)
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
        bool macroCouldDeclare = false;
        var wholeWord = new Regex($@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);

        int depth = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            Tok t = tokens[i];
            if (t.Kind == TokKind.Directive)
            {
                // Only a #define can write code (a #line names a file, which may hold the word).
                if (rawText && string.Equals(DirectiveKeyword(t.Text), "define", StringComparison.Ordinal)
                    && (wholeWord.IsMatch(t.Text) || t.Text.Contains("##", StringComparison.Ordinal)))
                {
                    macroCouldDeclare = true;
                }
                continue;
            }
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
            bool calledShape = i + 1 < tokens.Count && tokens[i + 1].Is("(");
            if (!calledShape)
            {
                // In the view every macro is expanded, so a use without '(' declares nothing. In
                // raw text it may be a macro's argument (`DECLARE(PS)`).
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

            // Not a declaration: an expression in a global initializer (`= PS(`) is a call; a
            // type-like token before the name is a declaration this reading failed on.
            Tok? previous = i > 0 ? tokens[i - 1] : null;
            if (previous is { Kind: TokKind.Identifier } || previous?.Text is ">" or "]")
                unread.Add(at);
            else
                calls.Add(at);
        }

        return new Analysis(prototypes, definitions, calls, unread, macroCouldDeclare);
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

        // A semantic (`: COLOR0`), then the terminator.
        int k = close + 1;
        while (k < tokens.Count && (tokens[k].Kind is TokKind.Identifier or TokKind.Number || tokens[k].Text is ":" or "(" or ")" or ","))
            k++;
        if (k >= tokens.Count || !(tokens[k].Is(";") || tokens[k].Is("{")))
            return false;
        last = k;

        declaration = new Declaration(
            Start: tokens[first].Offset,
            End: tokens[k].Offset + 1,
            NameLine: 0,
            NameColumn: 0,
            ReturnKey: ReturnKey(tokens.GetRange(first, nameIndex - first), typedefs),
            ParameterKey: ParameterKey(tokens.GetRange(nameIndex + 2, close - nameIndex - 2), typedefs));
        return true;
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
            if (c == '\n')
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
                while (i < text.Length && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length && (text[i + 1] == '\n' || (text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n')))
                        i = text.IndexOf('\n', i) + 1;
                    else
                        i++;
                }
                tokens.Add(new Tok(TokKind.Directive, text[start..i], start));
                continue;
            }

            lineStart = false;
            if (rawText && c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
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
                while (i < text.Length && text[i] != '"' && text[i] != '\n')
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

    private static string DirectiveKeyword(string directive)
    {
        Match m = DirectiveName().Match(directive);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    /// <summary>Line starts of a text, for (line, column) positions; '\r' is part of a line's end.</summary>
    private sealed class LineIndex
    {
        private readonly List<int> _starts = [0];
        private readonly string _text;

        public LineIndex(string text)
        {
            _text = text;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
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

        /// <summary>The offset of a 1-based line and 0-based column, when that column is on the line.</summary>
        public int? Offset(int line, int column)
        {
            if (line < 1 || line > _starts.Count)
                return null;
            int start = _starts[line - 1];
            int end = line < _starts.Count ? _starts[line] - 1 : _text.Length;
            if (end > start && _text[end - 1] == '\r')
                end--;
            return column <= end - start ? start + column : null;
        }

        public bool IsBlankLine(string text, int line)
        {
            if (line < 1 || line > _starts.Count)
                return true;
            int start = _starts[line - 1];
            int end = line < _starts.Count ? _starts[line] : text.Length;
            return string.IsNullOrWhiteSpace(text[start..end]);
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
