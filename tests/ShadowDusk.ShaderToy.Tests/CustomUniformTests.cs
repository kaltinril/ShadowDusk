using Shouldly;
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
        r.Success.ShouldBeTrue(string.Format(
            "the shader is in-subset; diagnostics: {0}",
            string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"))));
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

        fx.ShouldContain("float uIntensity;", Case.Sensitive);
        fx.ShouldContain("float3 uTint;", Case.Sensitive);

        r.UsedUniforms.ShouldContain("uIntensity");
        r.UsedUniforms.ShouldContain("uTint");
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
        r.Fx!.ShouldContain("float3x3 uRot;", Case.Sensitive);
        r.UsedUniforms.ShouldContain("uRot");
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

        fx.ShouldContain("texture uNoiseTexture;", Case.Sensitive);
        fx.ShouldContain("sampler2D uNoise = sampler_state", Case.Sensitive);
        fx.ShouldContain("Texture = <uNoiseTexture>;", Case.Sensitive);
        fx.ShouldContain("tex2D(uNoise, uv)", Case.Sensitive);

        r.UsedUniforms.ShouldContain("uNoise");
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
        r.Fx!.ShouldContain("float uUnused;", Case.Sensitive);
        r.UsedUniforms.ShouldContain("uUnused");
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
        fx.ShouldContain("float iTime;", Case.Sensitive);
        fx.ShouldContain("sin(iTime)", Case.Sensitive);
        fx.ShouldNotContain("float u_time;");

        r.UsedUniforms.ShouldContain("iTime");
        r.UsedUniforms.ShouldNotContain("u_time");
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
        r.Success.ShouldBeFalse();
        r.Fx.ShouldBeNull();

        var error = r.Diagnostics.Single(d => d.Severity == DiagnosticSeverity.Error);
        error.Line.ShouldBeGreaterThan(0);
        error.Column.ShouldBeGreaterThan(0);
        error.Message.ShouldContain("sampler", Shouldly.Case.Insensitive);
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
        r.Success.ShouldBeFalse();
        r.Diagnostics.ShouldContain(d =>
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
        r.Fx!.ShouldContain("float uK = 1.0;", Case.Sensitive);
        r.UsedUniforms.ShouldContain("uK");
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
        r.Success.ShouldBeFalse();
        r.Diagnostics.ShouldContain(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("RENDERSIZE", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomVaryingOfCustomName_IsIgnored_NotEmitted()
    {
        // Phase 46 (second batch): a top-level `varying`/`in`/`attribute` is web/desktop-export
        // vertex-stage leftover the converter now IGNORES (not a reject). It is not emitted as a
        // global/parameter; an UNreferenced non-coordinate varying simply vanishes.
        const string glsl = """
        varying vec2 vCustom;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(0.0);
        }
        """;

        ConvertResult r = Convert(glsl);
        r.Success.ShouldBeTrue();
        r.Fx!.ShouldNotContain("vCustom", Case.Sensitive);
        r.UsedUniforms.ShouldNotContain("vCustom");
        r.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void CustomOutOfNonVec4Name_StillRejects()
    {
        // A top-level `out` of a custom name is NOT the supported plain-GLSL `out vec4 <name>;` fragment
        // output, and `out` is not a vertex-stage input, so it stays a loud reject.
        const string glsl = """
        out vec2 vBad;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(0.0);
        }
        """;

        ConvertResult r = Convert(glsl);
        r.Success.ShouldBeFalse();
        r.Diagnostics.ShouldContain(d => d.Severity == DiagnosticSeverity.Error);
    }
}
