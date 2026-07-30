using Shouldly;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Unit tests that assert specific HLSL output for hand-written minimal ShaderToy snippets. Each
/// targets one of the translation "traps" documented in <c>MAPPING.md</c>: a place where a naive
/// pass-through would emit subtly-wrong HLSL. These are pure (no disk, no native deps): they call
/// <see cref="ShaderToyConverter.Convert(string, ConvertOptions?)"/> and assert on substrings of the
/// emitted <c>.fx</c>.
/// </summary>
public sealed class TranslationTrapTests
{
    /// <summary>Wrap a snippet body inside a minimal valid <c>mainImage</c> and convert it.</summary>
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
            "the snippet should be in-subset; diagnostics: {0}",
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"))));
        result.Fx.ShouldNotBeNull();
        return result.Fx!;
    }

    [Fact]
    public void VecTypes_SpelledAsFloatN()
    {
        string fx = ConvertBody("    vec2 a = vec2(1.0, 2.0); vec3 b = vec3(a, 3.0); vec4 c = vec4(b, 4.0); fragColor = c;");

        // The GLSL type spellings must be rewritten to the HLSL floatN spellings.
        fx.ShouldContain("float2", Case.Sensitive);
        fx.ShouldContain("float3", Case.Sensitive);
        fx.ShouldContain("float4", Case.Sensitive);
        fx.ShouldNotContain("vec2 ", Case.Sensitive);
        fx.ShouldNotContain("vec3 ", Case.Sensitive);
        fx.ShouldNotContain("vec4 ", Case.Sensitive);
    }

    [Fact]
    public void Mix_BecomesLerp()
    {
        string fx = ConvertBody("    fragColor = vec4(mix(0.0, 1.0, fragCoord.x), 0.0, 0.0, 1.0);");

        fx.ShouldContain("lerp(", Case.Sensitive);
        fx.ShouldNotContain("mix(", Case.Sensitive);
    }

    [Fact]
    public void Fract_BecomesFrac()
    {
        string fx = ConvertBody("    float f = fract(fragCoord.x); fragColor = vec4(f, f, f, 1.0);");

        fx.ShouldContain("frac(", Case.Sensitive);
        fx.ShouldNotContain("fract(", Case.Sensitive);
    }

    [Fact]
    public void TwoArgAtan_BecomesAtan2()
    {
        string fx = ConvertBody("    float a = atan(fragCoord.y, fragCoord.x); fragColor = vec4(a, a, a, 1.0);");

        fx.ShouldContain("atan2(", Case.Sensitive);
    }

    [Fact]
    public void SingleArgAtan_StaysAtan()
    {
        string fx = ConvertBody("    float a = atan(fragCoord.x); fragColor = vec4(a, a, a, 1.0);");

        fx.ShouldContain("atan(", Case.Sensitive);
        fx.ShouldNotContain("atan2(", Case.Sensitive);
    }

    [Fact]
    public void TextureSample_BecomesTex2D()
    {
        string fx = ConvertBody("    vec2 uv = fragCoord / iResolution.xy; fragColor = texture(iChannel0, uv);");

        fx.ShouldContain("tex2D(iChannel0,", Case.Sensitive);
        fx.ShouldNotContain("texture(", Case.Sensitive);
    }

    [Fact]
    public void Mod_EmitsGlslModHelper_NotBareFmod()
    {
        string fx = ConvertBody("    float m = mod(fragCoord.x, 10.0); fragColor = vec4(m, m, m, 1.0);");

        // mod() must route through the generated GLSL-equivalent helper, never HLSL's truncating fmod.
        fx.ShouldContain("glsl_mod(", Case.Sensitive);
        fx.ShouldContain("float  glsl_mod(float  x, float  y) { return x - y * floor(x / y); }", Case.Sensitive);
        fx.ShouldNotContain("fmod(", Case.Sensitive);
    }

    [Fact]
    public void Mod_HelperNotEmitted_WhenUnused()
    {
        string fx = ConvertBody("    fragColor = vec4(fragCoord.x, 0.0, 0.0, 1.0);");

        // The helper block is emitted only when mod was used.
        fx.ShouldNotContain("glsl_mod", Case.Sensitive);
    }

    [Fact]
    public void Mat2Rotation_EmitsMulWithFloat2x2_ReversedOperandOrder()
    {
        // The matrix-order trap (MAPPING.md trap 2): an inline mat2 rotation times a vector must
        // become mul(v, float2x2(...)) — operand order reversed, constructor re-spelled float2x2.
        string fx = ConvertBody(
            "    float c = cos(iTime); float s = sin(iTime); vec2 v = fragCoord.xy;\n" +
            "    vec2 r = mat2(c, -s, s, c) * v; fragColor = vec4(r, 0.0, 1.0);");

        fx.ShouldContain("mul(", Case.Sensitive);
        fx.ShouldContain("float2x2(", Case.Sensitive);
        // The vector operand v must appear as the FIRST argument to mul (mul(v, M)), i.e. before
        // the float2x2 constructor in the emitted call.
        int mulIdx = fx.IndexOf("mul(", StringComparison.Ordinal);
        int matIdx = fx.IndexOf("float2x2(", StringComparison.Ordinal);
        mulIdx.ShouldBeGreaterThan(-1);
        matIdx.ShouldBeGreaterThan(mulIdx, customMessage: "the float2x2 constructor must come after 'mul(' as the second operand");
    }

    [Fact]
    public void VecScalarSplat_IsExpanded()
    {
        // GLSL vec3(0.0) splats the scalar to all 3 components. HLSL has no single-scalar vector
        // constructor, so MAPPING.md expands it to a ((floatN)(scalar)) cast.
        string fx = ConvertBody("    vec3 z = vec3(0.0); fragColor = vec4(z, 1.0);");

        fx.ShouldContain("((float3)(0.0))", Case.Sensitive);
    }

    // ── B1: matrix compound assignment is transposed like binary `*` ──────────────

    [Fact]
    public void MatrixCompoundAssign_DesugarsToMul()
    {
        // B1: GLSL `v *= M` means `v = v*M` (row-vector times matrix). Under the converter's
        // `A*B → mul(B,A)` rule that is `v = mul(M, v)` — the matrix is the FIRST mul() argument and
        // the vector is the SECOND. (Inverting to `mul(v, M)` emits the transpose: a vertical mirror.)
        // It must never be the invalid `float2 *= float2x2`.
        string fx = ConvertBody(
            "    float c = cos(iTime), s = sin(iTime); mat2 m = mat2(c, -s, s, c);\n" +
            "    vec2 p = fragCoord.xy; p *= m; fragColor = vec4(p, 0.0, 1.0);");

        fx.ShouldContain("p = mul(m, p)", Case.Sensitive);
        fx.ShouldNotContain("mul(p, m)", Case.Sensitive);
        fx.ShouldNotContain("p *= m", Case.Sensitive);
    }

    [Fact]
    public void MatrixTimesMatrixCompoundAssign_PreservesOrder()
    {
        // `A *= B` (both mat2) means `A = A*B`; under `A*B → mul(B,A)` that is `A = mul(B, A)`.
        string fx = ConvertBody(
            "    mat2 a = mat2(1.0, 0.0, 0.0, 1.0); mat2 b = mat2(2.0, 0.0, 0.0, 2.0);\n" +
            "    a *= b; fragColor = vec4(a[0], a[1]);");

        fx.ShouldContain("a = mul(b, a)", Case.Sensitive);
        fx.ShouldNotContain("a *= b", Case.Sensitive);
    }

    [Fact]
    public void ScalarCompoundAssign_StaysComponentwise()
    {
        // A scalar/vector `*=` must remain a plain component-wise compound assignment (not mul()).
        string fx = ConvertBody("    vec2 p = fragCoord.xy; p *= 2.0; fragColor = vec4(p, 0.0, 1.0);");

        fx.ShouldContain("p *= 2.0", Case.Sensitive);
    }

    // ── B2: no double-wrapped equality parentheses ────────────────────────────────

    [Fact]
    public void ScalarEqualityCondition_IsNotDoubleParenthesized()
    {
        // B2: `if (a == 0.0)` must not become `if ((a == 0.0))` (fxc -Werror,-Wparentheses-equality).
        string fx = ConvertBody(
            "    float a = fragCoord.x; float v = 1.0; if (a == 0.0) { v = 0.0; } fragColor = vec4(v, a, 0.0, 1.0);");

        fx.ShouldContain("if (a == 0.0)", Case.Sensitive);
        fx.ShouldNotContain("if ((a == 0.0))", Case.Sensitive);
    }

    // ── B3: vector equality in a boolean context is scalarized with all()/any() ───

    [Fact]
    public void VectorEqualityCondition_WrappedWithAll()
    {
        // B3: a vector `==` in an `if` must be reduced with all(...).
        string fx = ConvertBody(
            "    vec2 m = iMouse.xy; float v = 0.0; if (m == vec2(0.0)) { v = 1.0; } fragColor = vec4(v, m, 1.0);");

        fx.ShouldContain("if (all(m == ", Case.Sensitive);
    }

    [Fact]
    public void VectorInequalityCondition_WrappedWithAny()
    {
        // B3: a vector `!=` in an `if` must be reduced with any(...).
        string fx = ConvertBody(
            "    vec2 m = iMouse.xy; float v = 0.0; if (m != vec2(0.0)) { v = 1.0; } fragColor = vec4(v, m, 1.0);");

        fx.ShouldContain("if (any(m != ", Case.Sensitive);
    }

    // ── B4: implicit vector truncation made explicit ──────────────────────────────

    [Fact]
    public void WiderVectorInitializer_IsTruncatedWithSwizzle()
    {
        // B4: assigning a vec4 (iMouse) into a vec2 must emit an explicit `.xy` truncation.
        string fx = ConvertBody("    vec2 a = iMouse; fragColor = vec4(a, 0.0, 1.0);");

        fx.ShouldContain("float2 a = (iMouse).xy", Case.Sensitive);
    }

    // ── B5: stray declaration modifiers do not survive after the type ─────────────

    [Fact]
    public void StrayDeclModifierAfterType_IsDropped()
    {
        // B5: a modifier after the type (`float const k`, `vec2 mediump uv`) must be dropped so the
        // emitted HLSL is a clean `type name` (modifiers-after-type are rejected by fxc/FNA).
        string fx = ConvertBody(
            "    float const k = 2.0; vec2 mediump uv = fragCoord; fragColor = vec4(uv * k, 0.0, 1.0);");

        fx.ShouldContain("float k = 2.0", Case.Sensitive);
        fx.ShouldContain("float2 uv = ", Case.Sensitive);
        fx.ShouldNotContain("const k", Case.Sensitive);
        fx.ShouldNotContain("mediump", Case.Sensitive);
    }

    // ── L1: redundant built-in re-declaration dropped; iGlobalTime aliased ────────

    [Fact]
    public void RedundantBuiltinUniformRedeclaration_IsDropped()
    {
        // L1 exception (a): `uniform float iTime;` re-declares a built-in the harness already emits;
        // it must be dropped and the shader must still convert.
        string glsl = """
        uniform float iTime;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(sin(iTime), 0.0, 0.0, 1.0);
        }
        """;

        ConvertResult result = ShaderToyConverter.Convert(glsl);
        result.Success.ShouldBeTrue(string.Format(
            "a redundant built-in uniform re-declaration must be dropped, not rejected; diagnostics: {0}",
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"))));
        // The harness emits exactly one `float iTime;` global; the re-declaration must not duplicate it.
        System.Text.RegularExpressions.Regex.Matches(result.Fx!, @"(?m)^float iTime;")
            .Count.ShouldBe(1);
    }

    [Fact]
    public void DeprecatedIGlobalTimeAlias_BecomesITime()
    {
        // L1 exception (b): `iGlobalTime` is the deprecated spelling of `iTime`.
        string fx = ConvertBody("    float t = iGlobalTime; fragColor = vec4(sin(t), 0.0, 0.0, 1.0);");

        fx.ShouldContain("iTime", Case.Sensitive);
        fx.ShouldNotContain("iGlobalTime", Case.Sensitive);
    }

    [Fact]
    public void DeprecatedIGlobalFrameAlias_BecomesIFrame()
    {
        // L1 exception (b): `iGlobalFrame` is the deprecated spelling of `iFrame`.
        string fx = ConvertBody("    float f = float(iGlobalFrame); fragColor = vec4(f, 0.0, 0.0, 1.0);");

        fx.ShouldContain("iFrame", Case.Sensitive);
        fx.ShouldNotContain("iGlobalFrame", Case.Sensitive);
    }

    [Fact]
    public void UnknownGlobalIdentifier_IsRejectedAtConvertTime()
    {
        // L1 (honesty): a free identifier that is not a built-in / local / const / user function must
        // be a clean located Error, never a silent pass-through to a compile error.
        string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / RENDERSIZE;
            fragColor = vec4(uv, 0.5, 1.0);
        }
        """;

        ConvertResult result = ShaderToyConverter.Convert(glsl);
        result.Success.ShouldBeFalse();
        result.Fx.ShouldBeNull();
        result.Diagnostics.ShouldContain(
            d => d.Severity == DiagnosticSeverity.Error
                 && d.Message.Contains("RENDERSIZE")
                 && d.Line > 0 && d.Column > 0, "the undeclared identifier must be rejected with a located diagnostic");
    }
}
