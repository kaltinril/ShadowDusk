#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.GLSL;
using Xunit;

namespace ShadowDusk.GLSL.Tests;

public sealed class MonoGameGlslRewriterTests
{
    private const string ExampleA = """
#version 140
#ifdef GL_ARB_shading_language_420pack
#extension GL_ARB_shading_language_420pack : require
#endif

uniform sampler2D _39;

in vec4 in_var_COLOR0;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    vec4 _29 = texture(_39, in_var_TEXCOORD0) * in_var_COLOR0;
    vec3 _36 = vec3(((_29.x + _29.y) + _29.z) * 0.3333333432674407958984375);
    out_var_SV_Target = vec4(_36.x, _36.y, _36.z, _29.w);
}
""";

    private const string ExampleB = """
#version 140
#ifdef GL_ARB_shading_language_420pack
#extension GL_ARB_shading_language_420pack : require
#endif

layout(binding = 0, std140) uniform type_Globals
{
    vec4 TintColor;
} _Globals;

uniform sampler2D _38;

in vec4 in_var_COLOR0;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = (texture(_38, in_var_TEXCOORD0) * in_var_COLOR0) * _Globals.TintColor;
}
""";

    [Fact]
    public void ExampleA_RewritesToLegacyGlsl()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);

        result.Glsl.Should().Contain("varying vec4 vTexCoord0;");
        result.Glsl.Should().Contain("varying vec4 vFrontColor;");
        result.Glsl.Should().Contain("uniform sampler2D ps_s0;");
        result.Glsl.Should().Contain("texture2D(ps_s0, vTexCoord0.xy)");
        result.Glsl.Should().Contain("gl_FragColor");

        // vec4 input -> no swizzle.
        result.Glsl.Should().Contain("* vFrontColor;");

        result.Glsl.Should().NotContain("#version");
        result.Glsl.Should().NotContain("in_var_");
        result.Glsl.Should().NotContain("out_var_");
        result.Glsl.Should().NotContain("GL_ARB_shading_language_420pack");
    }

    [Fact]
    public void ExampleA_HasNoModernQualifiersOrTextureFn()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);

        // No leftover "in "/"out " input/output qualifier declarations.
        result.Glsl.Should().NotContain("\nin ");
        result.Glsl.Should().NotContain("\nout ");

        // No bare texture( — only texture2D(.
        System.Text.RegularExpressions.Regex
            .IsMatch(result.Glsl, @"(?<![A-Za-z0-9_])texture\s*\(")
            .Should().BeFalse("only texture2D( should remain");
    }

    [Fact]
    public void ExampleA_SamplersAndNoUniforms()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);

        result.UniformRegisterCount.Should().Be(0);
        result.Samplers.Should().ContainSingle();
        result.Samplers[0].Slot.Should().Be(0);
        result.Samplers[0].Name.Should().Be("ps_s0");
    }

    [Fact]
    public void ExampleB_RewritesUniformBlock()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleB, ShaderStage.Pixel);

        result.Glsl.Should().Contain("uniform vec4 ps_uniforms_vec4[1];");
        result.Glsl.Should().Contain("ps_uniforms_vec4[0]");
        result.UniformRegisterCount.Should().Be(1);

        result.Glsl.Should().Contain("varying vec4 vTexCoord0;");
        result.Glsl.Should().Contain("varying vec4 vFrontColor;");
        result.Glsl.Should().Contain("uniform sampler2D ps_s0;");
        result.Glsl.Should().Contain("texture2D(ps_s0, vTexCoord0.xy)");
        result.Glsl.Should().Contain("gl_FragColor");

        result.Glsl.Should().NotContain("#version");
        result.Glsl.Should().NotContain("in_var_");
        result.Glsl.Should().NotContain("out_var_");
        result.Glsl.Should().NotContain("_Globals");
        result.Glsl.Should().NotContain("type_Globals");
    }

    [Fact]
    public void Vec4Input_NoSwizzle_Vec2Input_GetsXy()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);

        // vec2 TEXCOORD0 use should be truncated with .xy.
        result.Glsl.Should().Contain("vTexCoord0.xy");

        // vec4 COLOR0 use should NOT get a swizzle appended.
        result.Glsl.Should().NotContain("vFrontColor.xyzw");
        result.Glsl.Should().Contain("* vFrontColor;");
    }

    [Fact]
    public void PrecisionHeaderIsPrepended()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);

        result.Glsl.Should().StartWith("#ifdef GL_ES");
        result.Glsl.Should().Contain("precision mediump float;");
        result.Glsl.Should().Contain("precision mediump int;");
    }

    // ---- roundEven/round → floor(x+0.5) lowering (WebGL1 reach fix). ----
    // SPIRV-Cross emits roundEven() for HLSL `round` (DXC maps it to OpRoundEven),
    // a GLSL ES 3.00 / GL 1.30 builtin WebGL1 (GLSL ES 1.00) lacks. The rewriter
    // lowers it to floor(x+0.5) — valid everywhere and what mgfxc emits. The PS
    // below is the verbatim SPIRV-Cross GLSL for Pixelated.fx.
    private const string PixelatedRoundEven = """
#version 140
#ifdef GL_ARB_shading_language_420pack
#extension GL_ARB_shading_language_420pack : require
#endif

uniform sampler2D SPIRV_Cross_CombinedSpriteTextureSpriteTextureSampler;

in vec2 in_var_TEXCOORD0;
in vec4 in_var_COLOR0;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = texture(SPIRV_Cross_CombinedSpriteTextureSpriteTextureSampler, vec2(roundEven(in_var_TEXCOORD0.x * 32.0) * 0.03125, roundEven(in_var_TEXCOORD0.y * 32.0) * 0.03125));
}
""";

    [Fact]
    public void RoundEven_IsLoweredToFloorHalfUp_AndNoRoundEvenRemains()
    {
        var result = MonoGameGlslRewriter.Rewrite(PixelatedRoundEven, ShaderStage.Pixel);

        // roundEven() is GLSL ES 3.00+ only and MUST NOT survive — it makes the
        // shader fail to load in WebGL1 (KNI Reach profile).
        result.Glsl.Should().NotContain("roundEven", "roundEven is unavailable in GLSL ES 1.00 (WebGL1)");

        // Each roundEven(expr) becomes floor((expr) + 0.5).
        result.Glsl.Should().Contain("floor((vTexCoord0.x * 32.0) + 0.5)");
        result.Glsl.Should().Contain("floor((vTexCoord0.y * 32.0) + 0.5)");
    }

    [Fact]
    public void BareRound_IsLoweredToFloorHalfUp()
    {
        // Defensive: SPIRV-Cross can also emit bare round() (OpRound), also ES-3.00-only.
        const string src = """
#version 140

uniform sampler2D _10;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = texture(_10, vec2(round(in_var_TEXCOORD0.x), in_var_TEXCOORD0.y));
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        // No round(/roundEven( call survives; only floor( remains.
        System.Text.RegularExpressions.Regex
            .IsMatch(result.Glsl, @"\bround(Even)?\s*\(")
            .Should().BeFalse("round/roundEven are unavailable in GLSL ES 1.00 (WebGL1)");
        result.Glsl.Should().Contain("floor((vTexCoord0.x) + 0.5)");
    }

    [Fact]
    public void Round_NestedArgument_BalancedParensLoweredCorrectly()
    {
        // A nested call inside the argument must be captured by the balanced-paren scan.
        const string src = """
#version 140

uniform sampler2D _10;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = vec4(roundEven(abs(in_var_TEXCOORD0.x) * 8.0));
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("roundEven");
        result.Glsl.Should().Contain("floor((abs(vTexCoord0.x) * 8.0) + 0.5)");
    }

    [Fact]
    public void VertexStage_ReturnsInputUnchanged()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Vertex);

        result.Glsl.Should().Be(ExampleA);
        result.Samplers.Should().BeEmpty();
        result.UniformRegisterCount.Should().Be(0);
    }

    // ---- Slang-frontend (browser path) GLSL. SPIRV-Cross names interface vars after
    // Slang's field/entrypoint identifiers (input_Color, entryPointParam_MainPS,
    // GlobalParams_default/globalParams) instead of HLSL semantics; the rewriter's
    // Slang-normalization pre-pass must fold these into the same MojoShader dialect as
    // the DXC examples above. Captured verbatim from the Slang fidelity spike. ----

    private const string SlangGrayscale = """
#version 140
#ifdef GL_ARB_shading_language_420pack
#extension GL_ARB_shading_language_420pack : require
#endif

uniform sampler2D SPIRV_Cross_CombinedSpriteTextureSpriteTextureSampler;

in vec2 input_TextureCoordinates;
in vec4 input_Color;
out vec4 entryPointParam_MainPS;

void main()
{
    vec4 _29 = texture(SPIRV_Cross_CombinedSpriteTextureSpriteTextureSampler, input_TextureCoordinates) * input_Color;
    float _36 = ((_29.x + _29.y) + _29.z) * 0.3333333432674407958984375;
    vec4 _59 = _29;
    _59.x = _36;
    _59.y = _36;
    _59.z = _36;
    entryPointParam_MainPS = _59;
}
""";

    private const string SlangTint = """
#version 140
#ifdef GL_ARB_shading_language_420pack
#extension GL_ARB_shading_language_420pack : require
#endif

layout(binding = 0, std140) uniform GlobalParams_default
{
    vec4 TintColor;
} globalParams;

uniform sampler2D SPIRV_Cross_CombinedSpriteTextureSpriteTextureSampler;

in vec2 input_TextureCoordinates;
in vec4 input_Color;
out vec4 entryPointParam_MainPS;

void main()
{
    entryPointParam_MainPS = (texture(SPIRV_Cross_CombinedSpriteTextureSpriteTextureSampler, input_TextureCoordinates) * input_Color) * globalParams.TintColor;
}
""";

    [Fact]
    public void SlangGrayscale_NormalizesToLegacyGlsl()
    {
        var result = MonoGameGlslRewriter.Rewrite(SlangGrayscale, ShaderStage.Pixel);

        // input_TextureCoordinates -> in_var_TEXCOORD0 -> vTexCoord0; input_Color -> vFrontColor.
        result.Glsl.Should().Contain("varying vec4 vTexCoord0;");
        result.Glsl.Should().Contain("varying vec4 vFrontColor;");
        result.Glsl.Should().Contain("uniform sampler2D ps_s0;");
        result.Glsl.Should().Contain("texture2D(ps_s0, vTexCoord0.xy)");
        // entryPointParam_MainPS -> out_var_SV_Target -> gl_FragColor.
        result.Glsl.Should().Contain("gl_FragColor");

        // No Slang-isms or modern qualifiers survive.
        result.Glsl.Should().NotContain("input_");
        result.Glsl.Should().NotContain("entryPointParam_");
        result.Glsl.Should().NotContain("in_var_");
        result.Glsl.Should().NotContain("out_var_");
        result.Glsl.Should().NotContain("#version");

        result.Samplers.Should().ContainSingle();
        result.Samplers[0].Name.Should().Be("ps_s0");
        result.UniformRegisterCount.Should().Be(0);
    }

    [Fact]
    public void SlangTint_NormalizesUniformBlockAndInterface()
    {
        var result = MonoGameGlslRewriter.Rewrite(SlangTint, ShaderStage.Pixel);

        // GlobalParams_default { vec4 TintColor; } globalParams -> ps_uniforms_vec4[].
        result.Glsl.Should().Contain("uniform vec4 ps_uniforms_vec4[1];");
        result.Glsl.Should().Contain("ps_uniforms_vec4[0]");
        result.UniformRegisterCount.Should().Be(1);

        result.Glsl.Should().Contain("varying vec4 vTexCoord0;");
        result.Glsl.Should().Contain("varying vec4 vFrontColor;");
        result.Glsl.Should().Contain("uniform sampler2D ps_s0;");
        result.Glsl.Should().Contain("gl_FragColor");

        // No Slang-isms survive.
        result.Glsl.Should().NotContain("input_");
        result.Glsl.Should().NotContain("entryPointParam_");
        result.Glsl.Should().NotContain("globalParams");
        result.Glsl.Should().NotContain("GlobalParams_default");
        result.Glsl.Should().NotContain("type_Globals");
        result.Glsl.Should().NotContain("_Globals");
    }
}
