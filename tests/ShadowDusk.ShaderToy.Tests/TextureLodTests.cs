using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// <c>textureLod</c> lowering. A base-level <c>textureLod(s, uv, 0.)</c> lowers to a plain <c>tex2D</c>
/// (the legacy <c>tex2Dlod</c> intrinsic does not rewrite to a modern Texture method on the OpenGL/DirectX
/// targets, FX0012; the single-pass harness binds each iChannelN without mipmaps, so mip 0 is the only
/// level and the two are equivalent). A non-zero LOD is a loud, LOCATED convert-time reject — matching
/// the mip-bias form — instead of emitting <c>tex2Dlod</c> that fails downstream with an error pointing
/// at generated HLSL.
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
    public void TextureLod_WithNonZeroLod_RejectsLoudlyAtConvertTime()
    {
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              fragColor = textureLod(iChannel0, fragCoord / iResolution.xy, 2.0);
            }
            """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        // The tex2Dlod it used to emit does not compile through the OpenGL/DirectX pipeline
        // (FX0012), so the boundary is now rejected HERE, located in the user's GLSL, exactly like
        // the mip-bias texture(s, uv, bias) form.
        r.Success.Should().BeFalse("a non-zero LOD has no compilable mapping on the primary targets");
        r.Fx.Should().BeNull();
        ConvertDiagnostic error = r.Diagnostics.Should().ContainSingle(d =>
            d.Severity == DiagnosticSeverity.Error).Subject;
        error.Message.Should().Contain("textureLod").And.Contain("non-zero");
        error.Line.Should().BeGreaterThan(0, "the reject must point at the user's GLSL, not generated HLSL");
        error.Column.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TextureLod_WithNonLiteralLod_AlsoRejects()
    {
        // A runtime LOD expression cannot be proven zero, so it takes the same loud reject path.
        const string glsl = """
            void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
              float lod = iTime;
              fragColor = textureLod(iChannel0, fragCoord / iResolution.xy, lod);
            }
            """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);

        r.Success.Should().BeFalse();
        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("textureLod", StringComparison.Ordinal) && d.Line > 0);
    }
}
