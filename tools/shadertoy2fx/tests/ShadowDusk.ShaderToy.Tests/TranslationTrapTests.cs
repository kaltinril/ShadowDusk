using FluentAssertions;
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
        result.Success.Should().BeTrue(
            "the snippet should be in-subset; diagnostics: {0}",
            string.Join("; ", result.Diagnostics.Select(d => $"{d.Severity}:{d.Message}")));
        result.Fx.Should().NotBeNull();
        return result.Fx!;
    }

    [Fact]
    public void VecTypes_SpelledAsFloatN()
    {
        string fx = ConvertBody("    vec2 a = vec2(1.0, 2.0); vec3 b = vec3(a, 3.0); vec4 c = vec4(b, 4.0); fragColor = c;");

        // The GLSL type spellings must be rewritten to the HLSL floatN spellings.
        fx.Should().Contain("float2");
        fx.Should().Contain("float3");
        fx.Should().Contain("float4");
        fx.Should().NotContain("vec2 ");
        fx.Should().NotContain("vec3 ");
        fx.Should().NotContain("vec4 ");
    }

    [Fact]
    public void Mix_BecomesLerp()
    {
        string fx = ConvertBody("    fragColor = vec4(mix(0.0, 1.0, fragCoord.x), 0.0, 0.0, 1.0);");

        fx.Should().Contain("lerp(");
        fx.Should().NotContain("mix(");
    }

    [Fact]
    public void Fract_BecomesFrac()
    {
        string fx = ConvertBody("    float f = fract(fragCoord.x); fragColor = vec4(f, f, f, 1.0);");

        fx.Should().Contain("frac(");
        fx.Should().NotContain("fract(");
    }

    [Fact]
    public void TwoArgAtan_BecomesAtan2()
    {
        string fx = ConvertBody("    float a = atan(fragCoord.y, fragCoord.x); fragColor = vec4(a, a, a, 1.0);");

        fx.Should().Contain("atan2(");
    }

    [Fact]
    public void SingleArgAtan_StaysAtan()
    {
        string fx = ConvertBody("    float a = atan(fragCoord.x); fragColor = vec4(a, a, a, 1.0);");

        fx.Should().Contain("atan(");
        fx.Should().NotContain("atan2(");
    }

    [Fact]
    public void TextureSample_BecomesTex2D()
    {
        string fx = ConvertBody("    vec2 uv = fragCoord / iResolution.xy; fragColor = texture(iChannel0, uv);");

        fx.Should().Contain("tex2D(iChannel0,");
        fx.Should().NotContain("texture(");
    }

    [Fact]
    public void Mod_EmitsGlslModHelper_NotBareFmod()
    {
        string fx = ConvertBody("    float m = mod(fragCoord.x, 10.0); fragColor = vec4(m, m, m, 1.0);");

        // mod() must route through the generated GLSL-equivalent helper, never HLSL's truncating fmod.
        fx.Should().Contain("glsl_mod(");
        fx.Should().Contain("float  glsl_mod(float  x, float  y) { return x - y * floor(x / y); }");
        fx.Should().NotContain("fmod(");
    }

    [Fact]
    public void Mod_HelperNotEmitted_WhenUnused()
    {
        string fx = ConvertBody("    fragColor = vec4(fragCoord.x, 0.0, 0.0, 1.0);");

        // The helper block is emitted only when mod was used.
        fx.Should().NotContain("glsl_mod");
    }

    [Fact]
    public void Mat2Rotation_EmitsMulWithFloat2x2_ReversedOperandOrder()
    {
        // The matrix-order trap (MAPPING.md trap 2): an inline mat2 rotation times a vector must
        // become mul(v, float2x2(...)) — operand order reversed, constructor re-spelled float2x2.
        string fx = ConvertBody(
            "    float c = cos(iTime); float s = sin(iTime); vec2 v = fragCoord.xy;\n" +
            "    vec2 r = mat2(c, -s, s, c) * v; fragColor = vec4(r, 0.0, 1.0);");

        fx.Should().Contain("mul(");
        fx.Should().Contain("float2x2(");
        // The vector operand v must appear as the FIRST argument to mul (mul(v, M)), i.e. before
        // the float2x2 constructor in the emitted call.
        int mulIdx = fx.IndexOf("mul(", StringComparison.Ordinal);
        int matIdx = fx.IndexOf("float2x2(", StringComparison.Ordinal);
        mulIdx.Should().BeGreaterThan(-1);
        matIdx.Should().BeGreaterThan(mulIdx, "the float2x2 constructor must come after 'mul(' as the second operand");
    }

    [Fact]
    public void VecScalarSplat_IsExpanded()
    {
        // GLSL vec3(0.0) splats the scalar to all 3 components. HLSL has no single-scalar vector
        // constructor, so MAPPING.md expands it to a ((floatN)(scalar)) cast.
        string fx = ConvertBody("    vec3 z = vec3(0.0); fragColor = vec4(z, 1.0);");

        fx.Should().Contain("((float3)(0.0))");
    }
}
