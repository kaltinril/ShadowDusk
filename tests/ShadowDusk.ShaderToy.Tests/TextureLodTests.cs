using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// <c>textureLod</c> lowering. A base-level <c>textureLod(s, uv, 0.)</c> lowers to a plain <c>tex2D</c>
/// (the legacy <c>tex2Dlod</c> intrinsic does not rewrite to a modern Texture method on the OpenGL/DirectX
/// targets, FX0012; the single-pass harness binds each iChannelN without mipmaps, so mip 0 is the only
/// level and the two are equivalent). A non-zero LOD keeps the explicit <c>tex2Dlod</c> form.
/// </summary>
public sealed class TextureLodTests
{
    [Fact]
    public void TextureLod_WithZeroLod_LowersToTex2D()
    {
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              fragColor = textureLod(iChannel0, fragCoord / iResolution.xy, 0.);
            }
            """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        r.Success.Should().BeTrue(
            "diagnostics: {0}", string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}")));
        r.Fx!.Should().Contain("tex2D(iChannel0", "a base-level textureLod lowers to a plain tex2D");
        r.Fx!.Should().NotContain("tex2Dlod", "the unsupported tex2Dlod must not be emitted for lod 0");
    }

    [Fact]
    public void TextureLod_WithNonZeroLod_KeepsTex2Dlod()
    {
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              fragColor = textureLod(iChannel0, fragCoord / iResolution.xy, 2.0);
            }
            """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        r.Success.Should().BeTrue();
        r.Fx!.Should().Contain("tex2Dlod(iChannel0", "an explicit non-zero LOD keeps the tex2Dlod form");
    }
}
