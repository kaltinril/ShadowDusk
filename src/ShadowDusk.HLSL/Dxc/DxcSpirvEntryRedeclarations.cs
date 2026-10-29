#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Lexer;
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
/// converters, on every host (the browser's DXC is the same code).</para>
///
/// <para><b>Why removing the prototype is faithful.</b> A prototype adds nothing the definition
/// does not carry: DXC's own DXIL path takes the entry's semantics from the definition and
/// never from a prototype (a prototype's <c>TEXCOORD3</c> beside a definition's <c>COLOR1</c>
/// compiles to <c>COLOR1</c>, and a semantic written only on the prototype is "missing"), and
/// fxc (<c>mgfxc</c> 3.8.4.1) compiles these shapes. So a prototype whose return type and
/// parameter types match the single definition is blanked from the compile's input: replaced
/// by spaces, keeping every line break, so no diagnostic line or column moves. DXC then sees
/// one declaration and compiles the definition it would have compiled. An overload prototype
/// (different parameters, no definition of its own) of an entry function nothing calls is
/// inert, fxc compiles past it, and it is blanked too.</para>
///
/// <para><b>What is left to DXC, and what is refused.</b> No definition, or more than one,
/// and a prototype that differs from the definition only in its return type, are passed
/// through: DXC reports them cleanly (<c>missing entry point definition</c>,
/// <c>ambiguous entry point function</c>, <c>functions that differ only in their return
/// type</c>), as fxc refuses them (X3501, X3068). Three shapes cannot be made safe and are
/// refused with <c>SD0236</c>, loudly, instead of crashing: a call to the entry function
/// placed between its prototype and its definition (without the prototype the call has no
/// declaration; fxc compiles it), an overload prototype of an entry function the source
/// calls (which declaration a call means cannot be told from the text), and a prototype that
/// a macro expansion produces, which a text edit cannot reach.</para>
///
/// <para><b>Decided on the preprocessed text.</b> DXC compiles the text after its preprocessor,
/// so the declarations are counted on the managed preprocessed view
/// (<see cref="FxMacroPreprocessor.ProcessForCompiler"/>: conditionals evaluated, macros
/// expanded, one output line per input line). <c>#if SM6</c> / <c>#else</c> around two
/// definitions leaves one, as it does for DXC. The prototypes to drop are then blanked in the
/// raw text on the same lines; one the raw text does not hold came out of a macro. When the view
/// cannot be built, the raw text decides alone and a prototype beside several written
/// definitions is refused rather than guessed at.</para>
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
    /// <param name="entryPoint">The entry function DXC is asked to compile.</param>
    /// <param name="sourceFileName">Display name used in diagnostics.</param>
    public static Result<string, ShaderError> Prepare(string source, string entryPoint, string? sourceFileName)
    {
        ArgumentNullException.ThrowIfNull(source);
        string file = sourceFileName ?? string.Empty;
        if (string.IsNullOrEmpty(entryPoint) || !MentionsTwice(source, entryPoint))
            return Result<string, ShaderError>.Ok(source);

        // The raw text: every declaration written out, live or not. With no prototype in it and no
        // directive that could make one, there is no redeclaration for DXC to see.
        Analysis raw = Analyze(source, entryPoint);
        if (raw.Prototypes.Count == 0 && !raw.MacroCouldDeclare)
            return Result<string, ShaderError>.Ok(source);

        // What DXC compiles is the PREPROCESSED text: `#if SM6 … #else … #endif` around two
        // definitions leaves one, and a macro can produce a declaration. The decision is made on
        // the managed preprocessed view, which keeps one output line per input line, and each
        // prototype it says to drop is blanked in the raw text on the same line.
        Result<FxMacroPreprocessor.CompilerInput, ShaderError> view = FxMacroPreprocessor.ProcessForCompiler(source, file);
        if (view.IsSuccess && view.Value.PredefinedMacroUse is null)
        {
            string text = view.Value.Text;
            Result<IReadOnlyList<Declaration>, ShaderError> plan = Plan(Analyze(text, entryPoint), text, file, entryPoint);
            if (plan.IsFailure)
                return Result<string, ShaderError>.Fail(plan.Error);
            if (plan.Value.Count == 0)
                return Result<string, ShaderError>.Ok(source);

            var blank = new List<Declaration>();
            foreach (Declaration prototype in plan.Value)
            {
                Declaration? written = raw.Prototypes.FirstOrDefault(p => p.NameLine == prototype.NameLine);
                if (written is null)
                {
                    (string mappedFile, int mappedLine) = new SourceLineMap(text, file).Resolve(prototype.NameLine);
                    return Result<string, ShaderError>.Fail(new ShaderError(
                        File: mappedFile,
                        Line: mappedLine,
                        Column: 0,
                        Code: ErrorCode,
                        Message: $"entry point '{entryPoint}' is declared more than once, and a declaration on this line comes out " +
                                 $"of a macro expansion. {CrashSentence} A declaration a macro produces cannot be removed from the " +
                                 $"source, so write the prototype of '{entryPoint}' out, or remove it."));
                }
                blank.Add(written);
            }
            return Result<string, ShaderError>.Ok(Blank(source, blank));
        }

        // No trustworthy view (a directive the managed preprocessor does not model, or a test of
        // a compiler-predefined macro): decide on the raw text. Several written definitions then
        // cannot be told apart (one may sit in a dead #if branch, which would leave DXC one
        // definition and the crash), so a prototype beside them is refused, never guessed at.
        if (raw.Prototypes.Count > 0 && raw.Definitions.Count > 1)
        {
            Declaration prototype = raw.Prototypes[0];
            return Result<string, ShaderError>.Fail(Refuse(source, file, prototype.NameLine, prototype.NameColumn,
                $"entry point '{entryPoint}' has a prototype and more than one written definition, and which definition is " +
                $"compiled cannot be read from this source ({(view.IsFailure ? view.Error.Message : "a conditional tests a macro only the compiler defines")}). " +
                $"{CrashSentence} Remove the prototype of '{entryPoint}'."));
        }

        Result<IReadOnlyList<Declaration>, ShaderError> rawPlan = Plan(raw, source, file, entryPoint);
        if (rawPlan.IsFailure)
            return Result<string, ShaderError>.Fail(rawPlan.Error);
        return Result<string, ShaderError>.Ok(rawPlan.Value.Count > 0 ? Blank(source, rawPlan.Value) : source);
    }

    /// <summary>
    /// The prototypes to blank (empty when the source is to be compiled as is), or the refusal
    /// for a shape that cannot be compiled without the prototype.
    /// </summary>
    private static Result<IReadOnlyList<Declaration>, ShaderError> Plan(
        Analysis analysis, string source, string file, string entryPoint)
    {
        // No prototype: nothing to do. No definition or several: DXC reports that cleanly itself
        // ("missing entry point definition", "ambiguous entry point function").
        if (analysis.Prototypes.Count == 0 || analysis.Definitions.Count != 1)
            return Result<IReadOnlyList<Declaration>, ShaderError>.Ok([]);

        Declaration definition = analysis.Definitions[0];
        var blank = new List<Declaration>();
        foreach (Declaration prototype in analysis.Prototypes)
        {
            bool sameReturn = string.Equals(prototype.ReturnKey, definition.ReturnKey, StringComparison.Ordinal);
            bool sameParameters = string.Equals(prototype.ParameterKey, definition.ParameterKey, StringComparison.Ordinal);
            if (sameParameters && !sameReturn)
                continue; // DXC refuses this itself ("differ only in their return type"), as fxc does (X3068).
            if (!sameParameters && analysis.Calls.Count > 0)
            {
                return Result<IReadOnlyList<Declaration>, ShaderError>.Fail(Refuse(source, file, prototype.NameLine, prototype.NameColumn,
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
            foreach (Call call in analysis.Calls)
            {
                if (call.Offset > firstPrototype && call.Offset < definition.Start)
                {
                    return Result<IReadOnlyList<Declaration>, ShaderError>.Fail(Refuse(source, file, call.Line, call.Column,
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

    private static ShaderError Refuse(string source, string file, int line, int column, string message)
    {
        (string mappedFile, int mappedLine) = new SourceLineMap(source, file).Resolve(line);
        return new ShaderError(
            File: mappedFile,
            Line: mappedLine,
            Column: column,
            Code: ErrorCode,
            Message: message);
    }

    /// <summary>A cheap gate: a redeclaration needs the name at least twice.</summary>
    private static bool MentionsTwice(string source, string name)
    {
        int first = source.IndexOf(name, StringComparison.Ordinal);
        return first >= 0 && source.IndexOf(name, first + name.Length, StringComparison.Ordinal) >= 0;
    }

    /// <summary>A global-scope function declaration of the entry name.</summary>
    /// <param name="Start">Offset of the declaration's first token (attributes, modifiers, return type).</param>
    /// <param name="End">Offset one past the declaration's last character (a prototype's <c>;</c>).</param>
    /// <param name="NameLine">Physical line of the function name.</param>
    /// <param name="NameColumn">Column of the function name.</param>
    /// <param name="ReturnKey">The normalized return type.</param>
    /// <param name="ParameterKey">The normalized parameter types.</param>
    private sealed record Declaration(int Start, int End, int NameLine, int NameColumn, string ReturnKey, string ParameterKey);

    /// <summary>A use of the entry name that is not one of its declarations (a call).</summary>
    private sealed record Call(int Offset, int Line, int Column);

    private sealed record Analysis(
        IReadOnlyList<Declaration> Prototypes,
        IReadOnlyList<Declaration> Definitions,
        IReadOnlyList<Call> Calls,
        bool MacroCouldDeclare);

    private static Analysis Analyze(string source, string name)
    {
        IReadOnlyList<Token> tokens = new FxLexer(source, string.Empty).Tokenize();
        int[] offsets = FxPreParser.ComputeCharacterOffsets(source, tokens);

        var prototypes = new List<Declaration>();
        var definitions = new List<Declaration>();
        var calls = new List<Call>();
        bool macroCouldDeclare = false;
        var wholeWord = new Regex($@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);

        int depth = 0;
        int boundary = -1; // index of the last global-scope token that ends a declaration
        for (int i = 0; i < tokens.Count; i++)
        {
            Token t = tokens[i];
            switch (t.Kind)
            {
                case TokenKind.Preprocessor:
                    // A macro can produce a declaration of the name when its body names it, when it
                    // pastes tokens, or when it is function-like (`DECLARE(PS)` passes the name in).
                    if (wholeWord.IsMatch(t.Text)
                        || t.Text.Contains("##", StringComparison.Ordinal)
                        || FunctionLikeDefine().IsMatch(t.Text))
                    {
                        macroCouldDeclare = true;
                    }
                    if (depth == 0)
                        boundary = i;
                    continue;
                case TokenKind.LBrace:
                    depth++;
                    continue;
                case TokenKind.RBrace:
                    depth = Math.Max(0, depth - 1);
                    if (depth == 0)
                        boundary = i;
                    continue;
                case TokenKind.Semicolon:
                    if (depth == 0)
                        boundary = i;
                    continue;
            }

            if (t.Kind != TokenKind.Identifier
                || !string.Equals(t.Text, name, StringComparison.Ordinal)
                || i + 1 >= tokens.Count
                || tokens[i + 1].Kind != TokenKind.LParen)
            {
                continue;
            }

            if (depth == 0 && TryReadDeclaration(source, tokens, offsets, boundary, i, out Declaration? declaration, out bool hasBody, out int last))
            {
                (hasBody ? definitions : prototypes).Add(declaration!);
                if (hasBody)
                {
                    // The body's braces are walked by the main loop (it resumes at the '{').
                    i = last - 1;
                }
                else
                {
                    boundary = last;
                    i = last;
                }
                continue;
            }

            calls.Add(new Call(offsets[i], t.Line, t.Column));
        }

        return new Analysis(prototypes, definitions, calls, macroCouldDeclare);
    }

    /// <summary>
    /// Reads a global-scope function declaration whose name token is at <paramref name="nameIndex"/>:
    /// a type right before the name, a balanced parameter list, then optional semantic and
    /// <c>register(...)</c> tokens, ending at a <c>;</c> (a prototype) or a <c>{</c> (a definition).
    /// </summary>
    /// <param name="source">The text the tokens were read from.</param>
    /// <param name="tokens">The text's tokens.</param>
    /// <param name="offsets">Each token's character offset in <paramref name="source"/>.</param>
    /// <param name="boundary">Index of the last global-scope token that ended a declaration (-1 for none).</param>
    /// <param name="nameIndex">Index of the function name token.</param>
    /// <param name="declaration">The declaration read, when this returns true.</param>
    /// <param name="hasBody">True for a definition, false for a prototype.</param>
    /// <param name="last">The index of the <c>;</c> of a prototype, or of the <c>{</c> of a definition.</param>
    private static bool TryReadDeclaration(
        string source,
        IReadOnlyList<Token> tokens,
        int[] offsets,
        int boundary,
        int nameIndex,
        out Declaration? declaration,
        out bool hasBody,
        out int last)
    {
        declaration = null;
        hasBody = false;
        last = -1;

        // A type directly before the name, with nothing but whitespace between: `float4 PS(`,
        // `vector<float, 4> PS(`. An operator the lexer does not emit (`k + PS(`) leaves a gap
        // that is not blank, so an expression is never mistaken for a declaration.
        int previous = nameIndex - 1;
        if (previous <= boundary || tokens[previous].Kind is not (TokenKind.Identifier or TokenKind.RAngle))
            return false;
        int previousEnd = offsets[previous] + tokens[previous].Text.Length;
        if (!string.IsNullOrWhiteSpace(source[previousEnd..offsets[nameIndex]]))
            return false;

        // The balanced parameter list.
        int parens = 0;
        int close = -1;
        for (int j = nameIndex + 1; j < tokens.Count; j++)
        {
            if (tokens[j].Kind == TokenKind.LParen)
                parens++;
            else if (tokens[j].Kind == TokenKind.RParen && --parens == 0)
            {
                close = j;
                break;
            }
            else if (tokens[j].Kind is TokenKind.LBrace or TokenKind.RBrace or TokenKind.Semicolon or TokenKind.EOF)
                return false;
        }
        if (close < 0)
            return false;

        // Semantics and register clauses, then the terminator.
        int k = close + 1;
        while (k < tokens.Count && tokens[k].Kind is TokenKind.Identifier or TokenKind.Number or TokenKind.LParen
                   or TokenKind.RParen or TokenKind.Comma or TokenKind.LineComment or TokenKind.BlockComment)
        {
            k++;
        }
        if (k >= tokens.Count || tokens[k].Kind is not (TokenKind.Semicolon or TokenKind.LBrace))
            return false;

        hasBody = tokens[k].Kind == TokenKind.LBrace;
        last = k;

        // The declaration starts at the first non-comment token after the previous boundary.
        int first = boundary + 1;
        while (first < nameIndex && tokens[first].Kind is TokenKind.LineComment or TokenKind.BlockComment)
            first++;

        int start = offsets[first];
        // The lexer does not emit '[', so an attribute's opening bracket sits just before the
        // first token: take it in (never past the previous declaration's end).
        int floor = boundary >= 0 ? offsets[boundary] + tokens[boundary].Text.Length : 0;
        for (int p = start - 1; p >= floor; p--)
        {
            if (source[p] == '[')
                start = p;
            else if (!char.IsWhiteSpace(source[p]))
                break;
        }
        int end = offsets[k] + tokens[k].Text.Length;
        int open = offsets[nameIndex + 1];
        string returnText = source[start..offsets[nameIndex]];
        string parameterText = source[(open + 1)..offsets[close]];

        declaration = new Declaration(
            Start: start,
            End: end,
            NameLine: tokens[nameIndex].Line,
            NameColumn: tokens[nameIndex].Column,
            ReturnKey: ReturnKey(returnText),
            ParameterKey: ParameterKey(parameterText));
        return true;
    }

    /// <summary>The return type, without attributes, storage modifiers or spacing differences.</summary>
    private static string ReturnKey(string text)
    {
        string withoutAttributes = Attribute().Replace(StripComments(text), " ");
        string withoutModifiers = Modifier().Replace(withoutAttributes, " ");
        return Normalize(withoutModifiers);
    }

    /// <summary>
    /// The parameter types in order, without names, semantics, default values, an <c>in</c>
    /// modifier (the default) or spacing differences; a lone <c>void</c> is no parameters.
    /// </summary>
    private static string ParameterKey(string text)
    {
        string body = Normalize(StripComments(text));
        if (body.Length == 0 || string.Equals(body, "void", StringComparison.Ordinal))
            return string.Empty;

        var keys = new List<string>();
        foreach (string parameter in SplitTopLevel(body))
        {
            string p = parameter;
            int cut = p.IndexOfAny([':', '=']);
            if (cut >= 0)
                p = p[..cut];
            p = Normalize(p);

            // Brackets stay with the type (`float a[4]` and `float b[4]` are the same parameter type).
            string brackets = string.Empty;
            Match array = TrailingArray().Match(p);
            if (array.Success)
            {
                brackets = array.Value.Replace(" ", string.Empty, StringComparison.Ordinal);
                p = p[..array.Index].TrimEnd();
            }

            List<string> words = p.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count > 0 && string.Equals(words[0], "in", StringComparison.Ordinal))
                words.RemoveAt(0);
            if (words.Count >= 2)
                words.RemoveAt(words.Count - 1); // the parameter's name
            keys.Add(string.Join(' ', words) + brackets);
        }
        return string.Join(",", keys);
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '<' or '[')
                depth++;
            else if (c is ')' or '>' or ']')
                depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    private static string StripComments(string text) => Comment().Replace(text, " ");

    private static string Normalize(string text)
    {
        string spaced = Whitespace().Replace(text, " ").Trim();
        return Punctuation().Replace(spaced, "$1");
    }

    /// <summary>Replaces every character of each declaration with a space, keeping line breaks.</summary>
    private static string Blank(string source, IReadOnlyList<Declaration> declarations)
    {
        var builder = new StringBuilder(source);
        foreach (Declaration d in declarations)
        {
            for (int i = d.Start; i < d.End; i++)
            {
                if (builder[i] is not ('\r' or '\n'))
                    builder[i] = ' ';
            }
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"^\s*#\s*define\s+[A-Za-z_][A-Za-z0-9_]*\(", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionLikeDefine();

    [GeneratedRegex(@"\[[^\]]*\]", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:inline|static|precise|export|extern)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex Modifier();

    [GeneratedRegex(@"(?:\s*\[[^\]]*\])+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingArray();

    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s*([<>,\[\]()])\s*", RegexOptions.CultureInvariant)]
    private static partial Regex Punctuation();
}
