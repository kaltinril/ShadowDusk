using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Tests that <see cref="ConvertResult.UsedUniforms"/> reflects exactly which <c>iX</c> uniforms a
/// shader references, and that the emitted <c>.fx</c> declares only the used uniforms / channel
/// samplers (so a runtime helper drives the right effect parameters and the effect has no dead
/// globals). Note: <c>iResolution</c> is always declared in the <c>.fx</c> because the harness pixel
/// shader needs it to map uv → fragCoord, even when the body never names it.
/// </summary>
public sealed class UniformDetectionTests
{
    private static ConvertResult Convert(string body)
    {
        string glsl = $$"""
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
        {{body}}
        }
        """;
        ConvertResult result = ShaderToyConverter.Convert(glsl);
        result.Success.Should().BeTrue();
        return result;
    }

    [Fact]
    public void UsesTimeAndResolution_ListsThoseOnly_NotMouse()
    {
        ConvertResult result = Convert(
            "    vec2 uv = fragCoord / iResolution.xy; float t = iTime; fragColor = vec4(uv, t, 1.0);");

        result.UsedUniforms.Should().Contain("iResolution");
        result.UsedUniforms.Should().Contain("iTime");
        result.UsedUniforms.Should().NotContain("iMouse");
        result.UsedUniforms.Should().NotContain("iChannel0");
    }

    [Fact]
    public void EmittedFx_DeclaresUsedUniforms_AndOmitsUnused()
    {
        ConvertResult result = Convert(
            "    vec2 uv = fragCoord / iResolution.xy; float t = iTime; fragColor = vec4(uv, t, 1.0);");

        string fx = result.Fx!;
        fx.Should().Contain("float iTime;");
        fx.Should().Contain("float3 iResolution;");

        // Unreferenced uniforms must not be declared.
        fx.Should().NotContain("float4 iMouse;");
        fx.Should().NotContain("float iTimeDelta;");
        fx.Should().NotContain("int iFrame;");
    }

    [Fact]
    public void UsesMouse_ListsAndDeclaresMouse()
    {
        ConvertResult result = Convert(
            "    vec2 m = iMouse.xy; vec2 uv = fragCoord / iResolution.xy; fragColor = vec4(uv - m, 0.0, 1.0);");

        result.UsedUniforms.Should().Contain("iMouse");
        result.Fx!.Should().Contain("float4 iMouse;");
    }

    [Fact]
    public void Resolution_AlwaysDeclared_EvenWhenBodyNeverNamesIt()
    {
        // The body uses no uniform; iResolution is still declared because the harness PS needs it.
        ConvertResult result = Convert("    fragColor = vec4(fragCoord.x, fragCoord.y, 0.0, 1.0);");

        result.UsedUniforms.Should().NotContain("iResolution");
        result.Fx!.Should().Contain("float3 iResolution;");
    }

    [Fact]
    public void UsesChannel0Only_DeclaresChannel0Sampler_NotOthers()
    {
        ConvertResult result = Convert(
            "    vec2 uv = fragCoord / iResolution.xy; fragColor = texture(iChannel0, uv);");

        result.UsedUniforms.Should().Contain("iChannel0");
        result.UsedUniforms.Should().NotContain("iChannel1");

        string fx = result.Fx!;
        fx.Should().Contain("texture iChannel0Texture;");
        fx.Should().Contain("sampler2D iChannel0 = sampler_state");
        fx.Should().NotContain("iChannel1");
        fx.Should().NotContain("iChannel2");
        fx.Should().NotContain("iChannel3");
    }
}
