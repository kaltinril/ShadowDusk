using Shouldly;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// The B2/B3 boolean-context traps in ALL contexts (bug-hunt M16 follow-up). GLSL vector
/// <c>==</c>/<c>!=</c> yields a SINGLE bool everywhere — not just in if/while conditions — so the
/// reduction to <c>all(...)</c>/<c>any(...)</c> must also fire in assignments, initializers, returns,
/// call arguments, and <c>for</c>-loop conditions; and a <c>for</c>-condition, being a boolean
/// context, must not carry the extraneous parens that trip <c>-Werror,-Wparentheses-equality</c>.
/// Scalar compares (and operands inference cannot type) stay on the unchanged conservative path.
/// </summary>
public sealed class VectorEqualityTests
{
    private static string ConvertOk(string glsl)
    {
        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.ShouldBeTrue(string.Format(
            "the shader is in-subset; diagnostics: {0}",
            string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"))));
        r.Fx.ShouldNotBeNull();
        return r.Fx!;
    }

    // ── for-loop conditions route through EmitCondition (bug 1) ───────────────────────────────

    [Fact]
    public void ForCondition_TopLevelInequality_HasNoExtraneousParens()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float s = 0.0;
            for (int i = 0; i != 8; i++) { s += float(i); }
            fragColor = vec4(s / 28.0, 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        // The double-paren form `for (...; (i != 8); ...)` trips -Werror,-Wparentheses-equality.
        fx.ShouldContain("for (int i = 0; i != 8; i++)", Case.Sensitive);
        fx.ShouldNotContain("(i != 8)", Case.Sensitive);
    }

    [Fact]
    public void ForCondition_VectorCompare_ScalarizesWithAny()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 p = fragCoord;
            vec2 target = iResolution.xy;
            float n = 0.0;
            for (int i = 0; p != target; i++)
            {
                p = mix(p, target, 0.5);
                n += 1.0;
                if (n > 8.0) { break; }
            }
            fragColor = vec4(n / 8.0, 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("any(p != target)", Case.Sensitive, "a vector != in a for-condition is a single GLSL bool");
    }

    // ── vector ==/!= outside condition contexts (bug 2) ───────────────────────────────────────

    [Fact]
    public void Initializer_VectorEquality_ReducesToAll()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec3 p = vec3(fragCoord, 1.0);
            vec3 q = vec3(0.5);
            bool hit = (p == q);
            fragColor = vec4(hit ? 1.0 : 0.0, 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("bool hit = all(p == q)", Case.Sensitive, "GLSL vec3 == vec3 is a single bool; HLSL needs the all() reduction");
    }

    [Fact]
    public void Return_VectorEquality_ReducesToAll()
    {
        const string glsl = """
        bool same(vec2 a, vec2 b) { return a == b; }
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            bool s = same(fragCoord, iResolution.xy);
            fragColor = vec4(s ? 1.0 : 0.0, 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("return all(a == b);", Case.Sensitive);
    }

    [Fact]
    public void CallArgument_VectorInequality_ReducesToAny()
    {
        const string glsl = """
        float pick(bool b) { return b ? 1.0 : 0.0; }
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 p = fragCoord;
            vec2 q = iResolution.xy;
            float v = pick(p != q);
            fragColor = vec4(v, v, v, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("pick(any(p != q))", Case.Sensitive);
    }

    [Fact]
    public void Assignment_VectorEquality_ReducesToAll()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 p = fragCoord;
            vec2 q = iResolution.xy;
            bool hit = false;
            hit = (p == q);
            fragColor = vec4(hit ? 1.0 : 0.0, 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("hit = all(p == q)", Case.Sensitive);
    }

    // ── the conservative path is untouched ────────────────────────────────────────────────────

    [Fact]
    public void ScalarEquality_OutsideCondition_IsUnchanged()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float x = fragCoord.x;
            float y = fragCoord.y;
            bool b = (x == y);
            fragColor = vec4(b ? 1.0 : 0.0, 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("bool b = (x == y)", Case.Sensitive);
        fx.ShouldNotContain("all(", Case.Sensitive);
        fx.ShouldNotContain("any(", Case.Sensitive);
    }

    [Fact]
    public void ScalarInequality_InForCondition_IsUnchanged()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float s = 0.0;
            for (float t = 0.0; t != 4.0; t += 1.0) { s += t; }
            fragColor = vec4(s / 6.0, 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("t != 4.0;", Case.Sensitive);
        fx.ShouldNotContain("any(", Case.Sensitive);
    }
}
