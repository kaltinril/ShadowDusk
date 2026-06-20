using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Tests for top-level custom <c>uniform</c> support (Phase 46): a declared custom uniform is exposed
/// as an HLSL effect-parameter global, a custom <c>sampler2D</c> becomes a texture + sampler_state
/// pair, both appear in <see cref="ConvertResult.UsedUniforms"/>, and an unsupported-type uniform is a
/// loud, located reject. The L1 undeclared-identifier reject must still fire for a bare identifier that
/// was never declared.
/// </summary>
public sealed class CustomUniformTests
{
    private static ConvertResult Convert(string glsl) => ShaderToyConverter.Convert(glsl);

    private static ConvertResult ConvertOk(string glsl)
    {
        ConvertResult r = Convert(glsl);
        r.Success.Should().BeTrue(
            "the shader is in-subset; diagnostics: {0}",
            string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}")));
        return r;
    }

    [Fact]
    public void ScalarAndVectorUniforms_EmittedAsGlobals_AndReported()
    {
        const string glsl = """
        uniform float uIntensity;
        uniform vec3 uTint;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            fragColor = vec4(uTint * uIntensity * uv.x, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        string fx = r.Fx!;

        fx.Should().Contain("float uIntensity;");
        fx.Should().Contain("float3 uTint;");

        r.UsedUniforms.Should().Contain("uIntensity");
        r.UsedUniforms.Should().Contain("uTint");
    }

    [Fact]
    public void MatrixUniform_EmittedAsFloat3x3Global()
    {
        const string glsl = """
        uniform mat3 uRot;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            vec3 p = uRot * vec3(uv, 1.0);
            fragColor = vec4(p, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        r.Fx!.Should().Contain("float3x3 uRot;");
        r.UsedUniforms.Should().Contain("uRot");
    }

    [Fact]
    public void SamplerUniform_EmittedAsTextureSamplerPair_AndSampled()
    {
        const string glsl = """
        uniform sampler2D uNoise;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            fragColor = texture(uNoise, uv);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        string fx = r.Fx!;

        fx.Should().Contain("texture uNoiseTexture;");
        fx.Should().Contain("sampler2D uNoise = sampler_state");
        fx.Should().Contain("Texture = <uNoiseTexture>;");
        fx.Should().Contain("tex2D(uNoise, uv)");

        r.UsedUniforms.Should().Contain("uNoise");
    }

    [Fact]
    public void UnreferencedCustomUniform_StillReportedAndDeclared()
    {
        // A custom uniform is host-driven whether or not the body names it.
        const string glsl = """
        uniform float uUnused;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        r.Fx!.Should().Contain("float uUnused;");
        r.UsedUniforms.Should().Contain("uUnused");
    }

    [Fact]
    public void UTimeAlias_FoldsOntoITime()
    {
        const string glsl = """
        uniform float u_time;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(0.5 + 0.5 * sin(u_time), 0.0, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        string fx = r.Fx!;

        // The alias resolves to the built-in: iTime is declared and referenced; no u_time global.
        fx.Should().Contain("float iTime;");
        fx.Should().Contain("sin(iTime)");
        fx.Should().NotContain("float u_time;");

        r.UsedUniforms.Should().Contain("iTime");
        r.UsedUniforms.Should().NotContain("u_time");
    }

    [Fact]
    public void Sampler3DUniform_RejectsLoudly_WithLocation()
    {
        const string glsl = """
        uniform sampler3D uVolume;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(0.0);
        }
        """;

        ConvertResult r = Convert(glsl);
        r.Success.Should().BeFalse();
        r.Fx.Should().BeNull();

        var error = r.Diagnostics.Single(d => d.Severity == DiagnosticSeverity.Error);
        error.Line.Should().BeGreaterThan(0);
        error.Column.Should().BeGreaterThan(0);
        error.Message.Should().ContainEquivalentOf("sampler");
    }

    [Fact]
    public void NonSquareMatrixUniform_RejectsLoudly()
    {
        const string glsl = """
        uniform mat2x3 uXform;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(0.0);
        }
        """;

        ConvertResult r = Convert(glsl);
        r.Success.Should().BeFalse();
        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Line > 0 && d.Column > 0);
    }

    [Fact]
    public void UniformWithInitializer_IsAccepted_AndDefaultPreserved()
    {
        // G4: a `uniform` with a default value (valid GLSL 1.20+) is now ACCEPTED; the initializer is
        // emitted as the HLSL parameter's default so the consumer gets it unless they override.
        const string glsl = """
        uniform float uK = 1.0;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(uK);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        r.Fx!.Should().Contain("float uK = 1.0;");
        r.UsedUniforms.Should().Contain("uK");
    }

    [Fact]
    public void UndeclaredBareIdentifier_StillRejects_L1()
    {
        // L1 must still hold: a bare identifier never declared (not a uniform) is a loud reject.
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(RENDERSIZE.xy, 0.0, 1.0);
        }
        """;

        ConvertResult r = Convert(glsl);
        r.Success.Should().BeFalse();
        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("RENDERSIZE", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomVaryingOfCustomName_StillRejects()
    {
        // Only `uniform` is host-drivable; a `varying`/`in`/`out` custom name stays a reject.
        const string glsl = """
        varying vec2 vCustom;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(0.0);
        }
        """;

        ConvertResult r = Convert(glsl);
        r.Success.Should().BeFalse();
        r.Diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error);
    }
}
