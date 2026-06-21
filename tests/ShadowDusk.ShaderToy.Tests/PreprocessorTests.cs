using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Focused unit tests for the C-style <see cref="Preprocessor"/> (conditional compilation, the
/// const-expression evaluator, and object-/function-like macro expansion). These are pure (no disk):
/// they call <see cref="Preprocessor.Process"/> directly and assert on the preprocessed text, plus a
/// few end-to-end assertions through <see cref="ShaderToyConverter.Convert"/>. Line counts must be
/// preserved exactly so downstream diagnostics still point at the right source line.
/// </summary>
public sealed class PreprocessorTests
{
    private static string Pp(string src) => new Preprocessor().Process(src);

    [Fact]
    public void Ifdef_UndefinedMacro_BlockRemoved()
    {
        string outp = Pp("#ifdef FOO\nint kept = 1;\n#endif\nint always = 2;");
        outp.Should().NotContain("kept");
        outp.Should().Contain("always");
    }

    [Fact]
    public void Ifdef_DefinedMacro_BlockKept()
    {
        string outp = Pp("#define FOO\n#ifdef FOO\nint kept = 1;\n#endif");
        outp.Should().Contain("kept");
    }

    [Fact]
    public void Ifndef_UndefinedMacro_BlockKept()
    {
        string outp = Pp("#ifndef BAR\nint kept = 1;\n#endif");
        outp.Should().Contain("kept");
    }

    [Fact]
    public void If_ArithmeticTrue_BlockKept()
    {
        string outp = Pp("#if 1+1==2\nint kept = 1;\n#endif");
        outp.Should().Contain("kept");
    }

    [Fact]
    public void If_ArithmeticFalse_BlockRemoved()
    {
        string outp = Pp("#if 1+1==3\nint dropped = 1;\n#endif\nint kept = 2;");
        outp.Should().NotContain("dropped");
        outp.Should().Contain("kept");
    }

    [Fact]
    public void If_UndefinedIdentifier_EvaluatesToZero()
    {
        // Standard C rule: an undefined macro name in an #if expression is 0.
        string outp = Pp("#if NOT_DEFINED\nint dropped = 1;\n#else\nint kept = 2;\n#endif");
        outp.Should().NotContain("dropped");
        outp.Should().Contain("kept");
    }

    [Fact]
    public void If_MacroExpandedInExpression()
    {
        string outp = Pp("#define N 4\n#if N > 2\nint kept = 1;\n#endif");
        outp.Should().Contain("kept");
    }

    [Theory]
    [InlineData("#if 8 >> 1 == 4\nok\n#endif")]            // shift then compare
    [InlineData("#if (2 | 1) == 3\nok\n#endif")]           // bitwise or
    [InlineData("#if 7 % 3 == 1\nok\n#endif")]             // modulo
    [InlineData("#if 1 ? 1 : 0\nok\n#endif")]              // ternary
    [InlineData("#if ~0 == -1\nok\n#endif")]               // bitwise not
    [InlineData("#if 0x10 == 16\nok\n#endif")]             // hex literal
    public void If_OperatorMix_TrueBranchKept(string src)
    {
        Pp(src).Should().Contain("ok");
    }

    [Fact]
    public void Defined_Operator_BothForms()
    {
        string src = "#define A\n#if defined(A) && !defined B\nint kept = 1;\n#endif";
        Pp(src).Should().Contain("kept");
    }

    [Fact]
    public void Nested_Conditionals_SelectCorrectBranch()
    {
        string src =
            "#define MODE 2\n" +
            "#if MODE == 1\nbad1\n#elif MODE == 2\n#if 0\nbad2\n#else\ngoodInner\n#endif\n#else\nbad3\n#endif";
        string outp = Pp(src);
        outp.Should().Contain("goodInner");
        outp.Should().NotContain("bad1");
        outp.Should().NotContain("bad2");
        outp.Should().NotContain("bad3");
    }

    [Fact]
    public void ObjectMacro_Expands()
    {
        string outp = Pp("#define PI 3.14159\nfloat x = PI;");
        outp.Should().Contain("3.14159");
        outp.Should().NotContain("PI");
    }

    [Fact]
    public void FunctionMacro_ExpandsAtCallSite()
    {
        // A multi-token argument is hygienically wrapped in parentheses, so 'a + b' -> '(a + b)'
        // before it lands in the (x) slots of the body.
        string outp = Pp("#define SQR(x) ((x) * (x))\nfloat y = SQR(a + b);");
        outp.Should().Contain("(((a + b)) * ((a + b)))");
    }

    [Fact]
    public void FunctionMacro_MultiArgAndNestedCommas()
    {
        // A comma inside nested parens must not split arguments. A non-atom argument (a call) is
        // hygienically parenthesized so call-site precedence is preserved.
        string outp = Pp("#define MIXC(a, b, t) mix(a, b, t)\nvec3 c = MIXC(p, q, f(u, v));");
        outp.Should().Contain("mix(p, q, (f(u, v)))");
    }

    [Fact]
    public void FunctionMacro_NotFollowedByParen_LeftAsIs()
    {
        // A function-like macro name not followed by '(' is not an invocation (C rule).
        string outp = Pp("#define F(x) (x)\nint F = 3;");
        outp.Should().Contain("int F = 3;");
    }

    [Fact]
    public void Define_TrailingLineComment_NotPartOfBody()
    {
        // `#define X 0 // note` must define X as `0`, not `0 // note`; an #if X then evaluates cleanly.
        string outp = Pp("#define X 0 // enable feature\n#if X\nbad\n#else\ngood\n#endif");
        outp.Should().Contain("good");
        outp.Should().NotContain("bad");
    }

    [Fact]
    public void If_TrailingComment_Ignored()
    {
        string outp = Pp("#if 1 /* on */\nkept\n#endif");
        outp.Should().Contain("kept");
    }

    [Fact]
    public void Undef_StopsExpansion()
    {
        string outp = Pp("#define K 2.0\nfloat a = K;\n#undef K\nfloat b = K;");
        outp.Should().Contain("float a = 2.0;");
        outp.Should().Contain("float b = K;");
    }

    [Fact]
    public void LineCount_PreservedAcrossDirectivesAndInactiveBranches()
    {
        string src = "#define FOO\nline2\n#ifdef FOO\nline4\n#else\nline6\n#endif\nline8";
        string outp = Pp(src);
        outp.Split('\n').Should().HaveCount(8, "every physical source line maps to one output line");
        string[] lines = outp.Split('\n');
        lines[1].Should().Be("line2");
        lines[3].Should().Be("line4");
        lines[5].Should().BeEmpty("the inactive #else branch is blanked");
        lines[7].Should().Be("line8");
    }

    [Fact]
    public void LineContinuation_Backslash_IsFolded()
    {
        // A macro body split across two physical lines via '\' is one logical define.
        string outp = Pp("#define LONG 1 + \\\n2\nint x = LONG;");
        outp.Should().Contain("1 + 2");
    }

    [Fact]
    public void UnterminatedIf_IsRejected()
    {
        Action act = () => Pp("#if 1\nint x = 1;");
        act.Should().Throw<ConvertException>().WithMessage("*#endif*");
    }

    [Fact]
    public void EndifWithoutIf_IsRejected()
    {
        Action act = () => Pp("int x = 1;\n#endif");
        act.Should().Throw<ConvertException>().WithMessage("*#endif*");
    }

    [Fact]
    public void TokenPaste_InMacroBody_IsRejected()
    {
        Action act = () => Pp("#define CAT(a, b) a ## b\n");
        act.Should().Throw<ConvertException>().WithMessage("*##*");
    }

    [Fact]
    public void Include_IsRejected()
    {
        Action act = () => Pp("#include \"common.glsl\"\n");
        act.Should().Throw<ConvertException>().WithMessage("*#include*");
    }

    [Fact]
    public void SelfReferentialMacro_FollowsCRule_NotReExpanded()
    {
        // Per the standard C "blue-paint" rule, a macro that references ITSELF is expanded EXACTLY ONCE:
        // the macro's own name in its expansion is left as the plain identifier, NOT re-expanded. So
        // `#define A A + 1` expands `A` to `A + 1` (and stops), rather than looping forever. This is the
        // correct behavior; the previous runaway-reject was a false positive.
        string outp = Pp("#define A A + 1\nint x = A;");
        outp.Should().Contain("int x = A + 1;");
    }

    [Fact]
    public void MutuallyRecursiveMacros_FollowCRule_Terminate()
    {
        // Indirect self-reference (`#define A B` / `#define B A`) also terminates per the hide-set rule:
        // expanding A -> B -> (A is hidden) leaves A. No runaway.
        string outp = Pp("#define A B\n#define B A\nint x = A;");
        outp.Should().Contain("int x = A;");
    }

    [Fact]
    public void EndToEnd_IfdefGatedCode_ConvertsAndExcludesInactiveBranch()
    {
        string glsl =
            "#define AA\n" +
            "void mainImage(out vec4 fragColor, in vec2 fragCoord) {\n" +
            "#ifdef AA\n" +
            "  float v = 1.0;\n" +
            "#else\n" +
            "  float v = 0.123456;\n" +
            "#endif\n" +
            "  fragColor = vec4(v, v, v, 1.0);\n}";
        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.Should().BeTrue(string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        r.Fx.Should().NotContain("0.123456", "the inactive #else branch must not reach the emitted .fx");
    }

    [Fact]
    public void EndToEnd_FunctionMacro_Converts()
    {
        string glsl =
            "#define SQR(x) ((x) * (x))\n" +
            "void mainImage(out vec4 fragColor, in vec2 fragCoord) {\n" +
            "  vec2 uv = fragCoord / iResolution.xy;\n" +
            "  float v = SQR(uv.x);\n" +
            "  fragColor = vec4(v, v, v, 1.0);\n}";
        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.Should().BeTrue(string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        // SQR(uv.x) expands to (uv.x) * (uv.x); the emitter then drops the redundant atom parens.
        r.Fx.Should().Contain("uv.x * uv.x");
    }
}
