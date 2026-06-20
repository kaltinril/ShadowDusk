using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Unit coverage for the Phase 46 LOW-RISK / HIGH-YIELD converter batch (from a 160-shader failure
/// analysis): sized arrays in all three contexts + the GLSL array constructor / brace initializer,
/// bitwise operators (binary + compound assign), gl_FragCoord as a body built-in, uint/uvec -> int
/// mapping, the redundant `uniform sampler2D iChannelN` redeclaration drop, the redundant built-in
/// with an initializer drop, multi-declarator uniforms, and the openFrameworks header-token strip.
/// Each asserts the emitted HLSL for a hand-written minimal snippet (or a located reject when unsure).
/// </summary>
public sealed class Phase46BatchTests
{
    private static string ConvertOk(string glsl)
    {
        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.Should().BeTrue(
            "the shader is in-subset; diagnostics: {0}",
            string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}")));
        r.Fx.Should().NotBeNull();
        return r.Fx!;
    }

    private static ConvertResult ConvertReject(string glsl)
    {
        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.Should().BeFalse();
        r.Fx.Should().BeNull();
        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Line > 0 && d.Column > 0,
            "a reject must carry a located error");
        return r;
    }

    // ── 1: sized arrays in all 3 contexts + the GLSL array constructor / brace init ──────────

    [Fact]
    public void GlobalConstArray_SizeAfterType_WithBraceInit_BecomesStaticConstArray()
    {
        const string glsl = """
        const vec3[2] s = { vec3(0.1, 0.2, 0.9), vec3(0.9, 0.4, 0.1) };
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(mix(s[0], s[1], 0.5), 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.Should().Contain("static const float3 s[2] = { float3(0.1, 0.2, 0.9), float3(0.9, 0.4, 0.1) };");
    }

    [Fact]
    public void LocalArray_SizeAfterType_WithBraceInit_IsEmitted()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            vec2[2] c = { vec2(0.0, 0.0), vec2(1.0, 1.0) };
            float d = distance(uv, c[0]) + distance(uv, c[1]);
            fragColor = vec4(d, 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.Should().Contain("float2 c[2] = { float2(0.0, 0.0), float2(1.0, 1.0) };");
    }

    [Fact]
    public void ArrayParameter_SizeAfterType_EmitsSizeOnName()
    {
        const string glsl = """
        void scale(inout float[3] k, float s)
        {
            for (int i = 0; i < 3; i++) { k[i] = k[i] * s; }
        }
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float a[3];
            a[0] = fragCoord.x; a[1] = fragCoord.y; a[2] = 1.0;
            scale(a, 0.5);
            fragColor = vec4(a[0], a[1], a[2], 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        // HLSL spells the array size on the declarator NAME, not the type.
        fx.Should().Contain("void scale(inout float k[3], float s)");
    }

    [Fact]
    public void ArrayParameter_SizeAfterName_AlsoSupported()
    {
        const string glsl = """
        float sum3(float k[3])
        {
            return k[0] + k[1] + k[2];
        }
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float a[3];
            a[0] = fragCoord.x; a[1] = fragCoord.y; a[2] = 1.0;
            fragColor = vec4(sum3(a), 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.Should().Contain("float sum3(float k[3])");
    }

    [Fact]
    public void ArrayConstructor_UnsizedAndSized_BecomeBraceLists()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float a[3] = float[](0.2, 0.5, 0.3);
            vec2 b[2] = vec2[2](vec2(0.0), vec2(1.0));
            fragColor = vec4(a[0] + a[1] + a[2], b[0].x, b[1].y, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.Should().Contain("float a[3] = { 0.2, 0.5, 0.3 };");
        fx.Should().Contain("float2 b[2] = { ((float2)(0.0)), ((float2)(1.0)) };");
    }

    [Fact]
    public void ArraySize_OnBothTypeAndName_Rejects()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float[3] a[3];
            fragColor = vec4(a[0], 0.0, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.Should().Contain(d => d.Message.Contains("OR the name", StringComparison.Ordinal));
    }

    // ── 2: bitwise operators (binary + compound assign) ─────────────────────────────────────

    [Fact]
    public void BitwiseBinaryOperators_PassThrough()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            int x = int(fragCoord.x);
            int h = (x & 7) | (x ^ 3);
            int s = (x << 2) >> 1;
            fragColor = vec4(float(h + s) / 255.0, 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.Should().Contain("(x & 7)");
        fx.Should().Contain("(x ^ 3)");
        fx.Should().Contain("(x << 2)");
        fx.Should().Contain(">> 1");
    }

    [Fact]
    public void BitwiseCompoundAssign_PassThrough()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            int h = int(fragCoord.x);
            h &= 255;
            h |= 1;
            h ^= 7;
            h <<= 2;
            h >>= 1;
            fragColor = vec4(float(h) / 255.0, 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.Should().Contain("h &= 255");
        fx.Should().Contain("h |= 1");
        fx.Should().Contain("h ^= 7");
        fx.Should().Contain("h <<= 2");
        fx.Should().Contain("h >>= 1");
    }

    [Fact]
    public void LogicalAndBitwise_StayDistinct()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            int x = int(fragCoord.x);
            bool b = (x > 0) && (x < 10);
            int m = x & 1;
            fragColor = vec4(b ? float(m) : 0.0, 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.Should().Contain("&&");
        fx.Should().Contain("(x & 1)");
    }

    // ── 3: gl_FragCoord as a body built-in ──────────────────────────────────────────────────

    [Fact]
    public void GlFragCoord_InMainImageBody_Resolves()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = gl_FragCoord.xy / iResolution.xy;
            fragColor = vec4(uv, gl_FragCoord.z, gl_FragCoord.w);
        }
        """;

        string fx = ConvertOk(glsl);
        // The body's gl_FragCoord references resolve; the harness publishes the static and sets it.
        fx.Should().Contain("static float4 gl_FragCoord;");
        fx.Should().Contain("gl_FragCoord = float4(fragCoord, 0.0, 1.0);");
        fx.Should().Contain("gl_FragCoord.xy");
    }

    [Fact]
    public void GlFragCoord_NotUsed_NoStaticEmitted_InShaderToyMode()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.Should().NotContain("static float4 gl_FragCoord;");
    }

    // ── 4: uint / uvec -> int mapping ───────────────────────────────────────────────────────

    [Fact]
    public void UintAndUvec_MapToInt()
    {
        const string glsl = """
        uint h(uint x) { return x ^ (x >> 5); }
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            uvec2 p = uvec2(int(fragCoord.x), int(fragCoord.y));
            uint v = h(p.x + p.y);
            fragColor = vec4(float(v & 255) / 255.0, 0.0, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.Should().Contain("int h(int x)");
        fx.Should().Contain("int2 p =");
        fx.Should().NotContain("uint");
        fx.Should().NotContain("uvec");
    }

    // ── 5: redundant `uniform sampler2D iChannelN` redeclaration ─────────────────────────────

    [Fact]
    public void RedundantIChannelSampler_IsDropped_NotEmittedTwice()
    {
        const string glsl = """
        uniform sampler2D iChannel0;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = texture(iChannel0, fragCoord / iResolution.xy);
        }
        """;

        string fx = ConvertOk(glsl);
        // The harness still emits the built-in channel exactly once; the redeclaration is dropped
        // (it is NOT exposed as a custom uniform).
        fx.Split("sampler2D iChannel0").Length.Should().Be(2, "iChannel0 sampler declared exactly once");
        fx.Should().Contain("tex2D(iChannel0,");

        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.UsedUniforms.Should().Contain("iChannel0");
    }

    [Fact]
    public void CustomSamplerOfNonIChannelName_StillBecomesCustomParam()
    {
        const string glsl = """
        uniform sampler2D uNoise;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = texture(uNoise, fragCoord / iResolution.xy);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.Should().Contain("texture uNoiseTexture;");
        fx.Should().Contain("sampler2D uNoise = sampler_state");
    }

    // ── 6: redundant built-in with initializer + multi-declarator uniforms ──────────────────

    [Fact]
    public void RedundantBuiltinWithInitializer_IsDropped()
    {
        const string glsl = """
        uniform vec3 iResolution = vec3(1920.0, 1080.0, 1.0);
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        // The harness injects iResolution exactly once; the initializer value is irrelevant and dropped.
        fx.Should().Contain("float3 iResolution;");
        fx.Should().NotContain("1920.0");
    }

    [Fact]
    public void MultiDeclaratorUniforms_EachBecomesItsOwnCustomUniform()
    {
        const string glsl = """
        uniform float uA, uB, uC;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float v = uA + uB + uC;
            fragColor = vec4(v, v, v, 1.0);
        }
        """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.Should().BeTrue();
        string fx = r.Fx!;
        fx.Should().Contain("float uA;");
        fx.Should().Contain("float uB;");
        fx.Should().Contain("float uC;");
        r.UsedUniforms.Should().Contain("uA").And.Contain("uB").And.Contain("uC");
    }

    [Fact]
    public void MultiDeclaratorUniforms_WithDefault_OnLastDeclarator()
    {
        const string glsl = """
        uniform float uA, uB = 2.0;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(uA, uB, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.Should().Contain("float uA;");
        fx.Should().Contain("float uB = 2.0;");
    }

    // ── 7: openFrameworks header token strip ────────────────────────────────────────────────

    [Fact]
    public void OpenFrameworksHeaderToken_IsStripped()
    {
        const string glsl = """
        OF_GLSL_SHADER_HEADER
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.Should().NotContain("OF_GLSL_SHADER_HEADER");
    }

    [Fact]
    public void PragmaHeader_IsStripped()
    {
        const string glsl = """
        #pragma header
        #pragma optimize(on)
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.Should().NotContain("#pragma header");
    }
}
