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

    [Fact]
    public void VertexStage_ReturnsInputUnchanged()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Vertex);

        result.Glsl.Should().Be(ExampleA);
        result.Samplers.Should().BeEmpty();
        result.UniformRegisterCount.Should().Be(0);
    }
}
