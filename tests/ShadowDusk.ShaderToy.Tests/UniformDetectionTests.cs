using Shouldly;
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
        result.Success.ShouldBeTrue();
        return result;
    }

    [Fact]
    public void UsesTimeAndResolution_ListsThoseOnly_NotMouse()
    {
        ConvertResult result = Convert(
            "    vec2 uv = fragCoord / iResolution.xy; float t = iTime; fragColor = vec4(uv, t, 1.0);");

        result.UsedUniforms.ShouldContain("iResolution");
        result.UsedUniforms.ShouldContain("iTime");
        result.UsedUniforms.ShouldNotContain("iMouse");
        result.UsedUniforms.ShouldNotContain("iChannel0");
    }

    [Fact]
    public void EmittedFx_DeclaresUsedUniforms_AndOmitsUnused()
    {
        ConvertResult result = Convert(
            "    vec2 uv = fragCoord / iResolution.xy; float t = iTime; fragColor = vec4(uv, t, 1.0);");

        string fx = result.Fx!;
        fx.ShouldContain("float iTime;", Case.Sensitive);
        fx.ShouldContain("float3 iResolution;", Case.Sensitive);

        // Unreferenced uniforms must not be declared.
        fx.ShouldNotContain("float4 iMouse;", Case.Sensitive);
        fx.ShouldNotContain("float iTimeDelta;", Case.Sensitive);
        fx.ShouldNotContain("int iFrame;", Case.Sensitive);
    }

    [Fact]
    public void UsesMouse_ListsAndDeclaresMouse()
    {
        ConvertResult result = Convert(
            "    vec2 m = iMouse.xy; vec2 uv = fragCoord / iResolution.xy; fragColor = vec4(uv - m, 0.0, 1.0);");

        result.UsedUniforms.ShouldContain("iMouse");
        result.Fx!.ShouldContain("float4 iMouse;", Case.Sensitive);
    }

    [Fact]
    public void Resolution_AlwaysDeclared_EvenWhenBodyNeverNamesIt()
    {
        // The body uses no uniform; iResolution is still declared because the harness PS needs it.
        ConvertResult result = Convert("    fragColor = vec4(fragCoord.x, fragCoord.y, 0.0, 1.0);");

        result.UsedUniforms.ShouldNotContain("iResolution");
        result.Fx!.ShouldContain("float3 iResolution;", Case.Sensitive);
    }

    [Fact]
    public void UsesChannel0Only_DeclaresChannel0Sampler_NotOthers()
    {
        ConvertResult result = Convert(
            "    vec2 uv = fragCoord / iResolution.xy; fragColor = texture(iChannel0, uv);");

        result.UsedUniforms.ShouldContain("iChannel0");
        result.UsedUniforms.ShouldNotContain("iChannel1");

        string fx = result.Fx!;
        fx.ShouldContain("texture iChannel0Texture;", Case.Sensitive);
        fx.ShouldContain("sampler2D iChannel0 = sampler_state", Case.Sensitive);
        fx.ShouldNotContain("iChannel1", Case.Sensitive);
        fx.ShouldNotContain("iChannel2", Case.Sensitive);
        fx.ShouldNotContain("iChannel3", Case.Sensitive);
    }
}
