#nullable enable

using ShadowDusk.HLSL.Preprocessing;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// Issue #426: a block comment is whitespace (C translation phase 3 replaces it by one space before
/// any directive is processed), so a <c>/* ... */</c> that spans lines inside a <c>#define</c> (or
/// any other directive) does NOT end the directive. The managed preprocessor kept the comment's
/// newlines to hold the line numbering, which ended the directive at the comment and spilled the
/// rest into the code (hiding the next declaration from the initializer reader). Both modes are
/// pinned, and so is the line numbering of everything after the comment. DXC's own <c>-P</c> agrees
/// with every expectation here (<c>Issue426BlockCommentInDirectiveDxcTests</c>).
/// </summary>
public sealed class FxMacroPreprocessorBlockCommentTests
{
    private static string Compiler(string flattened)
    {
        var result = FxMacroPreprocessor.ProcessForCompiler(flattened, "t.fx");
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "ok");
        return result.Value.Text;
    }

    private static string Plain(string flattened)
    {
        var result = FxMacroPreprocessor.Process(flattened, "t.fx");
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "ok");
        return result.Value;
    }

    /// <summary>The token stream, one space between tokens: what the compiler sees, spacing aside.</summary>
    private static string Spaced(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(text, @"([^\w\s])", " $1 "), @"\s+", " ").Trim();

    /// <summary>Whitespace runs collapsed, for a directive line passed through verbatim.</summary>
    private static string Collapsed(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    private static string[] Lines(string text) => text.Split('\n');

    // The shape the issue reports: the macro's value comes after a comment that spans lines.
    private const string ValueAfterComment = "#define SCALE /* the scale,\n   in pixels */ 2\nfloat R = SCALE;\nfloat G = 3;\n";

    [Fact]
    public void AMultiLineCommentInsideADefine_DoesNotEndIt_PlainView()
    {
        Spaced(Plain(ValueAfterComment)).ShouldBe("float R = 2 ; float G = 3 ;");
    }

    [Fact]
    public void AMultiLineCommentInsideADefine_DoesNotEndIt_CompilerInput()
    {
        string[] lines = Lines(Compiler(ValueAfterComment));

        lines.Length.ShouldBe(5);
        lines[0].Trim().ShouldBe("");
        lines[1].Trim().ShouldBe("", customMessage: "the comment's second line belongs to the #define, not to the code");
        Spaced(lines[2]).ShouldBe("float R = 2 ;");
        lines[3].ShouldBe("float G = 3;", customMessage: "a line after the directive keeps its line and its columns");
    }

    [Fact]
    public void ACommentThatSpansSeveralLines_AfterTheValue_KeepsLaterLinesInPlace()
    {
        const string src = "#define A 1 /* one\n two\n three */\n#define B /* x\n*/ A + 1\nfloat4 v = B;\n  float4 w;\n";

        string[] lines = Lines(Compiler(src));

        lines.Length.ShouldBe(8);
        for (int i = 0; i < 5; i++)
            lines[i].Trim().ShouldBe("", customMessage: $"line {i + 1} is part of a #define");
        Spaced(lines[5]).ShouldBe("float4 v = 1 + 1 ;");
        lines[6].ShouldBe("  float4 w;");
        Spaced(Plain(src)).ShouldBe("float4 v = 1 + 1 ; float4 w ;");
    }

    [Fact]
    public void AFunctionLikeMacroBody_ContinuesPastAMultiLineComment()
    {
        const string src = "#define MUL(a, b) (a) /* times\n */ * (b)\nfloat R = MUL(2, 3);\n";

        Spaced(Plain(src)).ShouldBe("float R = ( 2 ) * ( 3 ) ;");
    }

    [Fact]
    public void AMultiLineCommentInsideAConditional_DoesNotEndIt()
    {
        const string src = "#if 0 /* off,\n */ || 1\nfloat4 on;\n#endif\n";

        Spaced(Plain(src)).ShouldBe("float4 on ;");
    }

    [Fact]
    public void ABackslashContinuationAndAMultiLineComment_InOneDefine()
    {
        const string src = "#define A 1 + \\\n 2 /* c\n */ + 3\nfloat R = A;\n";

        Spaced(Plain(src)).ShouldBe("float R = 1 + 2 + 3 ;");
        string[] lines = Lines(Compiler(src));
        lines.Length.ShouldBe(5);
        Spaced(lines[3]).ShouldBe("float R = 1 + 2 + 3 ;");
    }

    [Fact]
    public void ACommentBeforeTheHash_StillLeavesADirective()
    {
        // Only whitespace (the comment) precedes the '#' on its logical line.
        const string src = "/* a\n b */ #define A 7\nfloat R = A;\n";

        Spaced(Plain(src)).ShouldBe("float R = 7 ;");
        Spaced(Lines(Compiler(src))[2]).ShouldBe("float R = 7 ;");
    }

    [Fact]
    public void ACommentInCode_StillKeepsTheCodeAfterItOnItsOwnLine()
    {
        // Outside a directive a newline is just whitespace, so compiler input keeps it: the code
        // after the comment stays on its line and column, where DXC reports it.
        const string src = "float4 a; /* c\n */ float4 b;\nfloat4 c;\n";

        string[] lines = Lines(Compiler(src));

        lines[0].ShouldBe("float4 a;     ");
        lines[1].ShouldBe("    float4 b;");
        lines[2].ShouldBe("float4 c;");
    }

    [Fact]
    public void AHashAfterACommentThatStartedInCode_IsNotADirective()
    {
        // Phase 3 makes lines 1 and 2 one line, so the '#' is not at the start of a line: the
        // preprocessor leaves it to the compiler (which rejects it), it does not define A.
        const string src = "float4 a; /* c\n */ #define A 1\n#ifdef A\nfloat4 defined;\n#endif\n";

        Spaced(Plain(src)).ShouldBe("float4 a ; # define A 1", customMessage: "the #ifdef must see A undefined");
    }

    [Fact]
    public void ACommentThatClosesOnTheLineAfterADirective_PassesThroughOnOneLine()
    {
        // '#pragma' goes to the compiler verbatim; joined, it stays one directive.
        const string src = "#pragma pack_matrix /* why:\n row major */ (row_major)\nfloat4 a;\n";

        string[] lines = Lines(Compiler(src));

        lines.Length.ShouldBe(4);
        Collapsed(lines[0]).ShouldBe("#pragma pack_matrix (row_major)");
        lines[1].ShouldBe("");
        lines[2].ShouldBe("float4 a;");
    }

    [Fact]
    public void ALineDirective_SpanningAComment_IsWrittenOnTheLineOfItsNumber()
    {
        // DXC numbers the line after the one '#line N' has its NUMBER on as N (measured with -P:
        // 'float4 a;' here is line 11), so compiler input writes the joined directive on that line.
        const string src = "#line 10 /* x\n */ \"main.fx\"\nfloat4 a;\n#else\n";

        string[] lines = Lines(Compiler(src.Replace("#else\n", "", StringComparison.Ordinal)));

        Collapsed(lines[0]).ShouldBe("#line 10 \"main.fx\"");
        lines[1].ShouldBe("");
        lines[2].ShouldBe("float4 a;");

        // The managed pass counts the same way: the stray #else is line 12 of main.fx.
        var plain = FxMacroPreprocessor.Process(src, "t.fx");
        plain.IsFailure.ShouldBeTrue();
        plain.Error.Line.ShouldBe(12);
        plain.Error.File.ShouldBe("main.fx");
    }

    [Fact]
    public void ALineDirective_WithItsNumberAfterTheComment_IsWrittenOnThatLaterLine()
    {
        const string src = "#line /* x\n */ 10 \"main.fx\"\nfloat4 a;\n";

        string[] lines = Lines(Compiler(src));

        lines[0].ShouldBe("");
        Collapsed(lines[1]).ShouldBe("#line 10 \"main.fx\"");
        lines[2].ShouldBe("float4 a;");
    }

    [Fact]
    public void ADiagnosticAfterTheComment_StillPointsAtItsOwnLine()
    {
        // Lines 1-3 are one #define; the stray #else is on line 5 of the source.
        const string src = "#define A /* one\n two\n */ 1\nfloat4 a;\n#else\n";

        var result = FxMacroPreprocessor.ProcessForCompiler(src, "t.fx");
        var plain = FxMacroPreprocessor.Process(src, "t.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0009");
        result.Error.Line.ShouldBe(5);
        plain.IsFailure.ShouldBeTrue();
        plain.Error.Line.ShouldBe(5);
    }

    [Fact]
    public void ADiagnosticInsideTheDirective_PointsAtItsFirstLine()
    {
        const string src = "float4 a;\n#if /* one\n two */ (\nfloat4 b;\n#endif\n";

        var result = FxMacroPreprocessor.Process(src, "t.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Line.ShouldBe(2);
    }

    [Theory]
    // DXC -P (Issue426BlockCommentInDirectiveTests): a number straight after a backslash-newline
    // is on the backslash's line, one after a space on the next line is on that line.
    [InlineData("#line \\\n40 \"bar.fx\"\nfloat4 a;\n#else\n", 41)]
    [InlineData("#line \\\n40 \\\n\"bar.fx\"\nfloat4 a;\n#else\n", 42)]
    [InlineData("#line \\\n 40 \"bar.fx\"\nfloat4 a;\n#else\n", 40)]
    public void ALineDirectiveAfterABackslashNewline_CountsFromTheLineItsNumberIsOn(string src, int declarationLine)
    {
        // 'float4 a;' is line declarationLine of bar.fx, so the stray #else after it is the next.
        FxMacroPreprocessor.Process(src, "t.fx").Error.Line.ShouldBe(declarationLine + 1);
        FxMacroPreprocessor.ProcessForCompiler(src, "t.fx").Error.Line.ShouldBe(declarationLine + 1);
    }

    [Fact]
    public void AVeryLongCommentInsideADefine_IsSplicedInOnePass()
    {
        // The comment's lines are spliced into one logical line; in compiler-input mode every
        // comment character is a space, so re-copying the line once per spliced line was
        // quadratic: this input took 30 s that way and takes about 150 ms with the linear splice
        // (measured 2026-10-28, both modes).
        const int commentLines = 50_000;
        var sb = new System.Text.StringBuilder("#define A 1 /*\n");
        string filler = new('x', 75);
        for (int i = 0; i < commentLines; i++)
            sb.Append(filler).Append('\n');
        sb.Append("*/ + 2\nfloat R = A;\n");
        string src = sb.ToString();

        string[] lines = Lines(Compiler(src));

        lines.Length.ShouldBe(commentLines + 4);
        lines[commentLines + 1].Trim().ShouldBe("", customMessage: "the comment's last line belongs to the #define");
        Spaced(lines[commentLines + 2]).ShouldBe("float R = 1 + 2 ;");
        Spaced(Plain(src)).ShouldBe("float R = 1 + 2 ;");
    }
}
