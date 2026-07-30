using System.Linq;
using Shouldly;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Regression guards for the ShaderToy front-end defects found in the 2026-07-27
/// full-project review. Both were silent: valid, common GLSL that converted to HLSL
/// meaning something else, or that failed conversion for no legitimate reason.
/// </summary>
public sealed class ReviewRegressionTests
{
    private static string ConvertBody(string body)
    {
        string glsl = $$"""
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
        {{body}}
        }
        """;

        ConvertResult result = ShaderToyConverter.Convert(glsl);
        result.Success.ShouldBeTrue(string.Format(
            "the snippet is in-subset; diagnostics: {0}",
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"))));
        return result.Fx!;
    }

    // ── Assignment / sequence sub-expressions must keep their parentheses ────────────

    [Fact]
    public void AssignmentInsideACondition_KeepsItsParentheses()
    {
        // The raymarching idiom. The parser drops the source's grouping parens and the
        // emitter added none, so this became `if (d = map(p) < 0.001)` — HLSL binds `<`
        // tighter than `=`, so `d` received a bool 0/1 instead of the distance and every
        // later use of it read garbage. Compiles clean, renders a different image.
        string fx = ConvertBody("""
            float d = 1.0;
            float p = fragCoord.x;
            if ((d = p * 0.5) < 0.001) { d = 0.0; }
            fragColor = vec4(d);
        """);

        // The assignment must be wrapped so `<` cannot bind tighter than `=`.
        fx.ShouldContain("((d = (p * 0.5)) < 0.001)", Case.Sensitive);
    }

    [Fact]
    public void AssignmentInsideABinaryExpression_KeepsItsParentheses()
    {
        // `float e = (acc = 1.0) + 2.0;` must leave acc at 1.0, not 3.0.
        string fx = ConvertBody("""
            float acc = 0.0;
            float e = (acc = 1.0) + 2.0;
            fragColor = vec4(acc, e, 0.0, 1.0);
        """);

        fx.ShouldContain("(acc = 1.0) + 2.0", Case.Sensitive);
        fx.ShouldNotContain("acc = 1.0 + 2.0", Case.Sensitive);
    }

    [Fact]
    public void StatementLevelAssignment_StaysUnparenthesized()
    {
        // The parens belong only in sub-expression position — an ordinary assignment
        // statement must not churn into `(fragColor = ...);`.
        string fx = ConvertBody("    fragColor = vec4(1.0, 0.0, 0.0, 1.0);");

        fx.ShouldContain("fragColor = float4(1.0, 0.0, 0.0, 1.0);", Case.Sensitive);
        fx.ShouldNotContain("(fragColor = float4(1.0, 0.0, 0.0, 1.0));", Case.Sensitive);
    }

    // ── A directive inside a skipped conditional group is not evaluated ──────────────

    [Fact]
    public void NestedIfInsideSkippedGroup_IsNotEvaluated()
    {
        // C11 6.10.1p6 (inherited by the GLSL preprocessor): a directive in a skipped
        // group is processed only far enough to track nesting; its remaining tokens are
        // never evaluated or diagnosed. Evaluating them rejected an entire shader over an
        // expression sitting in a dead `#if 0` branch that no real GLSL compiler reads.
        const string glsl = """
        #if 0
        #if 1.5
        this is never compiled
        #endif
        #endif
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(1.0);
        }
        """;

        ConvertResult result = ShaderToyConverter.Convert(glsl);

        result.Success.ShouldBeTrue(string.Format(
            "diagnostics: {0}",
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"))));
    }

    [Fact]
    public void MalformedIfdefInsideSkippedGroup_IsNotEvaluated()
    {
        const string glsl = """
        #ifdef NOT_DEFINED
        #ifdef 123
        unreachable
        #endif
        #endif
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(1.0);
        }
        """;

        ShaderToyConverter.Convert(glsl).Success.ShouldBeTrue();
    }

    [Fact]
    public void MalformedIfInAnACTIVEGroup_StillFailsLoudly()
    {
        // POSITIVE CONTROL: the evaluator must keep rejecting what it genuinely cannot
        // evaluate when the group is LIVE, or the short-circuit above would have silently
        // disabled the diagnostic everywhere.
        const string glsl = """
        #if 1.5
        #endif
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(1.0);
        }
        """;

        ShaderToyConverter.Convert(glsl).Success.ShouldBeFalse();
    }

    [Fact]
    public void SkippedGroupStillTracksNesting()
    {
        // The nesting bookkeeping must survive the short-circuit: the inner #endif closes
        // the inner #if, so the code after the OUTER #endif is live.
        const string glsl = """
        #if 0
        #ifdef ANYTHING
        dead
        #endif
        also dead
        #endif
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(0.25, 0.5, 0.75, 1.0);
        }
        """;

        ConvertResult result = ShaderToyConverter.Convert(glsl);

        result.Success.ShouldBeTrue();
        result.Fx!.ShouldNotContain("dead", Case.Sensitive);
        result.Fx!.ShouldContain("0.25", Case.Sensitive);
    }
}
