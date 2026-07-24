#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.GLSL;
using Xunit;

namespace ShadowDusk.GLSL.Tests;

/// <summary>
/// Unit coverage for the Phase-53 GL portability lint (<c>SD0400</c>–<c>SD0402</c>):
/// the compile-time warnings that replace what used to be silent runtime failures
/// (the engine's generic draw-time "Shader Compilation Failed" with the driver log
/// hidden). Every finding must be warning-severity — the lint never rejects output.
/// </summary>
public sealed class GlslPortabilityAnalyzerTests
{
    private static IReadOnlyList<ShaderError> AnalyzePixel(
        string glsl, bool passHasVertexShader = true)
        => GlslPortabilityAnalyzer.Analyze(
            glsl, ShaderStage.Pixel, passHasVertexShader, "shader.fx", "MainPS");

    // ---- SD0400: gradient op inside a divergent loop (issue #141). ----

    [Fact]
    public void Sd0400_GradientInsideLoopWithBreak_Flagged()
    {
        const string glsl = """
void main()
{
    float acc = 0.0;
    for (int i = 0; i < 8; i++)
    {
        if (acc > vTexCoord0.y)
        {
            break;
        }
        acc += abs(dFdx(vTexCoord0.x));
    }
    ps_oC0 = vec4(acc);
}
""";
        var findings = AnalyzePixel(glsl);

        findings.Should().ContainSingle(f => f.Code == "SD0400");
        var f = findings.Single(f => f.Code == "SD0400");
        f.Severity.Should().Be(ShaderErrorSeverity.Warning, "lint findings never reject output");
        f.Message.Should().Contain("dFdx").And.Contain("ANGLE");
    }

    [Fact]
    public void Sd0400_GradientInsideLoopWithDiscard_Flagged()
    {
        const string glsl = """
void main()
{
    for (int i = 0; i < 4; i++)
    {
        if (vTexCoord0.x > 0.5)
        {
            discard;
        }
        ps_oC0 = vec4(fwidth(vTexCoord0.y));
    }
}
""";
        AnalyzePixel(glsl).Should().Contain(f => f.Code == "SD0400");
    }

    [Fact]
    public void Sd0400_GradientNestedInsideTwoDivergentLoops_FlaggedOnce()
    {
        // Both the outer and inner loop have a conditional break, and the gradient
        // call sits inside both — this must surface ONE finding (the innermost loop),
        // not one per enclosing loop.
        const string glsl = """
void main()
{
    for (int i = 0; i < 8; i++)
    {
        if (vTexCoord0.x > 0.9)
        {
            break;
        }
        for (int j = 0; j < 4; j++)
        {
            if (vTexCoord0.y > 0.5)
            {
                break;
            }
            ps_oC0 = vec4(dFdx(vTexCoord0.x));
        }
    }
}
""";
        var findings = AnalyzePixel(glsl);

        findings.Should().ContainSingle(f => f.Code == "SD0400",
            "one gradient call nested in two divergent loops is one issue, not two");
    }

    [Fact]
    public void Sd0400_GradientOutsideLoop_NotFlagged()
    {
        const string glsl = """
void main()
{
    float w = fwidth(vTexCoord0.x);
    for (int i = 0; i < 8; i++)
    {
        if (w > 0.5)
        {
            break;
        }
        w += 0.125;
    }
    ps_oC0 = vec4(w);
}
""";
        AnalyzePixel(glsl).Should().NotContain(f => f.Code == "SD0400",
            "the derivative is computed BEFORE the loop — the recommended fix shape");
    }

    [Fact]
    public void Sd0400_LoopWithoutDivergentExit_NotFlagged()
    {
        const string glsl = """
void main()
{
    float acc = 0.0;
    for (int i = 0; i < 8; i++)
    {
        acc += abs(dFdx(vTexCoord0.x));
    }
    ps_oC0 = vec4(acc);
}
""";
        AnalyzePixel(glsl).Should().NotContain(f => f.Code == "SD0400",
            "ANGLE's zeroing applies only to loops with a divergent exit (break/discard)");
    }

    // ---- SD0401: PS-only pass reading interpolants SpriteBatch's VS never writes. ----

    [Fact]
    public void Sd0401_PsOnlyPass_ReadsTexCoord1_Flagged()
    {
        const string glsl = """
varying vec4 vFrontColor;
varying vec4 vTexCoord0;
varying vec4 vTexCoord1;

void main()
{
    ps_oC0 = vTexCoord0 * vTexCoord1 * vFrontColor;
}
""";
        var findings = AnalyzePixel(glsl, passHasVertexShader: false);

        findings.Should().ContainSingle(f => f.Code == "SD0401");
        var f = findings.Single(f => f.Code == "SD0401");
        f.Severity.Should().Be(ShaderErrorSeverity.Warning);
        f.Message.Should().Contain("vTexCoord1").And.Contain("TEXCOORD1").And.Contain("SpriteBatch");
        f.Message.Should().NotContain("vTexCoord0 (", "the SpriteEffect-provided varyings are not listed as missing");
    }

    [Fact]
    public void Sd0401_PassHasOwnVertexShader_NotFlagged()
    {
        const string glsl = """
varying vec4 vTexCoord1;

void main()
{
    ps_oC0 = vTexCoord1;
}
""";
        AnalyzePixel(glsl, passHasVertexShader: true).Should().NotContain(f => f.Code == "SD0401",
            "a pass with its own VS defines its own varying contract");
    }

    [Fact]
    public void Sd0401_OnlySpriteEffectVaryings_NotFlagged()
    {
        const string glsl = """
varying vec4 vFrontColor;
varying vec4 vTexCoord0;

void main()
{
    ps_oC0 = vTexCoord0 * vFrontColor;
}
""";
        AnalyzePixel(glsl, passHasVertexShader: false).Should().NotContain(f => f.Code == "SD0401");
    }

    [Fact]
    public void Sd0401_UnknownSemanticPassthrough_Flagged()
    {
        const string glsl = """
varying vec4 var_NORMAL0;

void main()
{
    ps_oC0 = var_NORMAL0;
}
""";
        var findings = AnalyzePixel(glsl, passHasVertexShader: false);
        findings.Should().ContainSingle(f => f.Code == "SD0401");
        findings.Single(f => f.Code == "SD0401").Message.Should().Contain("NORMAL0");
    }

    // ---- SD0402: loop shapes outside GLSL ES 1.00 Appendix A (issue #138). ----

    [Fact]
    public void Sd0402_HeaderlessFor_Flagged()
    {
        const string glsl = """
void main()
{
    int i = 0;
    for (;;)
    {
        if (i > 4) { break; }
        i++;
    }
    ps_oC0 = vec4(float(i));
}
""";
        var findings = AnalyzePixel(glsl);
        findings.Should().Contain(f => f.Code == "SD0402" && f.Message.Contains("header-less"));
    }

    [Fact]
    public void Sd0402_HeaderlessFor_NoLongerFlagged_AfterRule13Rewrite_ProvableBound_Issue138()
    {
        // Same shape as the Apos.Shapes Newton loop: the "runtime" trip count is
        // actually a ternary between two literals. Run through the real rewriter first
        // (as the pipeline does) — Rule 13 proves the bound and gives the header a
        // real constant, so the analyzer must no longer flag it.
        const string glsl = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float result;
    bool _553 = in_var_TEXCOORD0.x > 0.0;
    int _555 = _553 ? 0 : 12;
    int _564 = 0;
    for (;;)
    {
        if (_564 < _555)
        {
            result = float(_564);
            _564++;
            continue;
        }
        else
        {
            result = 1.0;
            break;
        }
    }
    out_var_SV_Target = vec4(result);
}
""";
        var rewritten = MonoGameGlslRewriter.Rewrite(glsl, ShaderStage.Pixel);

        AnalyzePixel(rewritten.Glsl).Should().NotContain(f => f.Code == "SD0402",
            "Rule 13 proves the loop's real ceiling and gives the header a literal bound");
    }

    [Fact]
    public void Sd0402_EmptyIncrementFor_Flagged()
    {
        // The GaussianBlur shape: constant bound, index advanced in the body.
        const string glsl = """
void main()
{
    vec4 acc = vec4(0.0);
    for (int _40 = 0; _40 < 15; )
    {
        acc += ps_uniforms_vec4[1 + _40];
        _40++;
        continue;
    }
    ps_oC0 = acc;
}
""";
        var findings = AnalyzePixel(glsl);
        findings.Should().Contain(f => f.Code == "SD0402" && f.Message.Contains("empty increment"));
    }

    [Fact]
    public void Sd0402_EmptyIncrementFor_NoLongerFlagged_AfterRule12Rewrite_Issue138()
    {
        // End-to-end: the SAME GaussianBlur shape as Sd0402_EmptyIncrementFor_Flagged,
        // but run through MonoGameGlslRewriter.Rewrite first (as the real pipeline
        // does). Rule 12 hoists the increment into the header, so the analyzer run
        // afterward — the same check a real compile performs — must no longer warn.
        const string glsl = """
#version 140

layout(binding = 0, std140) uniform type_Globals
{
    vec4 ps_uniforms_vec4[15];
} _Globals;

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    vec4 acc = vec4(0.0);
    for (int _40 = 0; _40 < 15; )
    {
        acc += _Globals.ps_uniforms_vec4[1 + _40];
        _40++;
        continue;
    }
    out_var_SV_Target = acc;
}
""";
        var rewritten = MonoGameGlslRewriter.Rewrite(glsl, ShaderStage.Pixel);

        AnalyzePixel(rewritten.Glsl).Should().NotContain(f => f.Code == "SD0402",
            "Rule 12 hoists the increment into the header, so the shape SD0402 flagged no longer exists");
    }

    [Fact]
    public void Sd0402_ConformantFor_NotFlagged()
    {
        const string glsl = """
void main()
{
    vec4 acc = vec4(0.0);
    for (int i = 0; i < 15; i++)
    {
        acc += ps_uniforms_vec4[1 + i];
    }
    ps_oC0 = acc;
}
""";
        AnalyzePixel(glsl).Should().NotContain(f => f.Code == "SD0402");
    }

    [Fact]
    public void Sd0402_Rule9bOneShotForm_NotFlagged()
    {
        // The exact loop Rule 9b synthesizes for a lowered one-shot do-while —
        // fully Appendix-A-conformant, must never self-flag.
        const string glsl = """
void main()
{
    for (int _spvonce_0 = 0; _spvonce_0 < 1; _spvonce_0++)
    {
        if (vTexCoord0.x > 0.5)
        {
            break;
        }
        ps_oC0 = vec4(1.0);
    }
}
""";
        AnalyzePixel(glsl).Should().NotContain(f => f.Code == "SD0402",
            "the rewriter's own Rule 9b output is Appendix-A-conformant");
    }

    [Fact]
    public void Sd0402_FunctionCallInCondition_ConformantHeader_NotFlagged()
    {
        // Parens inside the condition must not confuse the top-level header split.
        const string glsl = """
void main()
{
    vec4 acc = vec4(0.0);
    for (int i = 0; i < int(min(4.0, 8.0)); i++)
    {
        acc += vec4(0.25);
    }
    ps_oC0 = acc;
}
""";
        AnalyzePixel(glsl).Should().NotContain(f => f.Code == "SD0402");
    }

    [Fact]
    public void Sd0402_GenuineDoWhile_FlaggedOnce()
    {
        const string glsl = """
void main()
{
    int i = 0;
    do
    {
        i++;
    } while (i < 4);
    ps_oC0 = vec4(float(i));
}
""";
        var findings = AnalyzePixel(glsl).Where(f => f.Code == "SD0402").ToList();
        findings.Should().ContainSingle("the do-while's trailing 'while (...)' must not double-count");
        findings[0].Message.Should().Contain("do-while");
    }

    [Fact]
    public void Sd0402_WhileLoopFollowingAnIfBlock_IsStillFlagged()
    {
        // Regression: IsDoWhileTail used to return true for ANY `while` whose preceding
        // non-whitespace character was '}'. A `while` that merely follows an if-block was
        // therefore misread as a do-while's trailing clause and its SD0402 dropped —
        // silently losing the finding on the exact shape WebGL1 rejects.
        const string glsl = """
void main()
{
    int i = 0;
    if (vTexCoord0.x > 0.5)
    {
        i = 1;
    }
    while (i < 4)
    {
        i++;
    }
    ps_oC0 = vec4(float(i));
}
""";
        var findings = AnalyzePixel(glsl).Where(f => f.Code == "SD0402").ToList();

        findings.Should().ContainSingle("the while loop after an if-block is a real Appendix A violation");
        findings[0].Message.Should().Contain("while loop");
    }

    [Fact]
    public void Sd0400_GradientInOuterLoop_BreakOnlyInNestedInnerLoop_IsStillFlagged()
    {
        // Pins the DELIBERATE over-approximation documented in the analyzer: the outer
        // loop's own exit is uniform and the inner loop reconverges before the gradient,
        // so this is a surplus finding. It is the intended trade (a missed SD0400 renders
        // black gradients in every Windows browser). If this ever becomes a false-positive
        // complaint, narrowing it needs a browser render proof first.
        const string glsl = """
void main()
{
    for (int i = 0; i < 8; i++)
    {
        for (int j = 0; j < 4; j++)
        {
            if (vTexCoord0.y > 0.5)
            {
                break;
            }
        }
        ps_oC0 = vec4(dFdx(vTexCoord0.x));
    }
}
""";
        AnalyzePixel(glsl).Should().Contain(f => f.Code == "SD0400");
    }

    [Fact]
    public void Sd0402_WhileLoop_Flagged()
    {
        const string glsl = """
void main()
{
    int i = 0;
    while (i < 4)
    {
        i++;
    }
    ps_oC0 = vec4(float(i));
}
""";
        AnalyzePixel(glsl).Should().Contain(f => f.Code == "SD0402" && f.Message.Contains("while"));
    }

    [Fact]
    public void VertexStage_LoopShapesChecked_GradientAndInterpolantChecksSkipped()
    {
        // Loop-shape portability applies to the VS too (Reach loads the VS through
        // the same Appendix-A validator); the gradient/SpriteBatch checks are
        // pixel-stage-only concepts.
        const string glsl = """
varying vec4 vTexCoord3;

void main()
{
    int i = 0;
    for (;;)
    {
        if (i > 2) { break; }
        i++;
    }
    gl_Position = vec4(float(i));
}
""";
        var findings = GlslPortabilityAnalyzer.Analyze(
            glsl, ShaderStage.Vertex, passHasVertexShader: true, "shader.fx", "MainVS");

        findings.Should().Contain(f => f.Code == "SD0402");
        findings.Should().NotContain(f => f.Code == "SD0400" || f.Code == "SD0401");
    }

    [Fact]
    public void CleanShader_NoFindings()
    {
        const string glsl = """
varying vec4 vFrontColor;
varying vec4 vTexCoord0;

void main()
{
    ps_oC0 = texture2D(ps_s0, vTexCoord0.xy) * vFrontColor;
}
""";
        AnalyzePixel(glsl, passHasVertexShader: false).Should().BeEmpty();
    }
}
