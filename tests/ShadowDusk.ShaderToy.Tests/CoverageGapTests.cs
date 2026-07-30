using Shouldly;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Unit coverage for the Phase 46 coverage-gap closures: G1 (top-level mutable globals → HLSL
/// <c>static</c>), G3 (more built-in aliases; type-mismatched alias becomes a custom uniform, not a
/// wrong alias), G4 (custom uniform with a default initializer), and G5 (harmless preprocessor
/// directives ignored, <c>#include</c> still rejected). Each gap also keeps its loud-reject boundary.
/// </summary>
public sealed class CoverageGapTests
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

    // ── G1: top-level mutable globals ─────────────────────────────────────────

    [Fact]
    public void MutableGlobal_Bare_EmittedAsStatic()
    {
        const string glsl = """
        float gAccum;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            gAccum = fragCoord.x / iResolution.x;
            fragColor = vec4(gAccum, gAccum, gAccum, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        r.Fx!.ShouldContain("static float gAccum;");
        // A mutable global is internal state, NOT a host-driven parameter.
        r.UsedUniforms.ShouldNotContain("gAccum");
    }

    [Fact]
    public void MutableGlobal_WithInitializer_EmittedAsStaticWithInit()
    {
        const string glsl = """
        vec3 gTint = vec3(0.2, 0.4, 0.8);
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(gTint, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        r.Fx!.ShouldContain("static float3 gTint = float3(0.2, 0.4, 0.8);", Case.Sensitive);
    }

    [Fact]
    public void MutableGlobal_MultiDeclarator_EachEmitted()
    {
        const string glsl = """
        float gA = 0.0, gB = 1.0, gC;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            gC = gA + gB;
            fragColor = vec4(gC, gC, gC, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        string fx = r.Fx!;
        fx.ShouldContain("static float gA = 0.0;", Case.Sensitive);
        fx.ShouldContain("static float gB = 1.0;", Case.Sensitive);
        fx.ShouldContain("static float gC;", Case.Sensitive);
    }

    [Fact]
    public void MutableGlobal_UnsupportedType_RejectsLoudly()
    {
        const string glsl = """
        double gBad;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(0.0);
        }
        """;

        ConvertResult r = Convert(glsl);
        r.Success.ShouldBeFalse();
        r.Fx.ShouldBeNull();
        r.Diagnostics.ShouldContain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Line > 0 && d.Column > 0 &&
            d.Message.Contains("double", StringComparison.OrdinalIgnoreCase));
    }

    // ── G3: aliases ───────────────────────────────────────────────────────────

    [Fact]
    public void ExactTypeAlias_Time_FoldsOntoITime()
    {
        const string glsl = """
        uniform float time;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(0.5 + 0.5 * sin(time), 0.0, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        string fx = r.Fx!;
        fx.ShouldContain("float iTime;", Case.Sensitive);
        fx.ShouldContain("sin(iTime)", Case.Sensitive);
        fx.ShouldNotContain("float time;");
        r.UsedUniforms.ShouldContain("iTime");
        r.UsedUniforms.ShouldNotContain("time");
    }

    [Fact]
    public void TypeMismatchedAlias_BecomesCustomUniform_NotWrongAlias()
    {
        // glslViewer u_resolution is vec2; ShaderToy iResolution is vec3. It must NOT be silently
        // aliased to iResolution (which would change the shape a reference resolves to); instead it is
        // exposed verbatim as a custom uniform the host drives.
        const string glsl = """
        uniform vec2 u_resolution;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / u_resolution;
            fragColor = vec4(uv, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        string fx = r.Fx!;
        fx.ShouldContain("float2 u_resolution;", Case.Sensitive);
        r.UsedUniforms.ShouldContain("u_resolution");
    }

    // ── G4: custom uniform with default initializer ───────────────────────────

    [Fact]
    public void UniformWithDefault_PreservesInitializer()
    {
        const string glsl = """
        uniform float uGain = 1.5;
        uniform vec3 uColor = vec3(0.9, 0.3, 0.1);
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(uColor * uGain, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        string fx = r.Fx!;
        fx.ShouldContain("float uGain = 1.5;", Case.Sensitive);
        fx.ShouldContain("float3 uColor = float3(0.9, 0.3, 0.1);", Case.Sensitive);
        r.UsedUniforms.ShouldContain("uGain");
        r.UsedUniforms.ShouldContain("uColor");
    }

    [Fact]
    public void SamplerWithInitializer_RejectsLoudly()
    {
        const string glsl = """
        uniform sampler2D uTex = 0;
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

    // ── G5: harmless preprocessor directives ──────────────────────────────────

    [Fact]
    public void VersionAndExtension_Dropped_OutputConverts()
    {
        const string glsl = """
        #version 330 core
        #extension GL_OES_standard_derivatives : enable
        #pragma optimize(on)
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        string fx = r.Fx!;
        fx.ShouldNotContain("#version", Case.Sensitive);
        fx.ShouldNotContain("#extension", Case.Sensitive);
        fx.ShouldNotContain("#pragma optimize", Case.Sensitive);
    }

    [Fact]
    public void GlslViewerChannelDirective_Ignored()
    {
        const string glsl = """
        #iChannel0 "https://example.com/tex.png"
        #iKeyboard
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        r.Fx!.ShouldNotContain("#iChannel0", Case.Sensitive);
    }

    [Fact]
    public void Include_StillRejectsLoudly()
    {
        const string glsl = """
        #include "common.glsl"
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(0.0);
        }
        """;

        ConvertResult r = Convert(glsl);
        r.Success.ShouldBeFalse();
        r.Diagnostics.ShouldContain(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("#include", StringComparison.Ordinal));
    }

    [Fact]
    public void UndeclaredIdentifier_StillRejects()
    {
        // The L1 guarantee: a genuinely-undeclared identifier (not a built-in, alias, uniform, local,
        // const, mutable global, or user function) is still a loud reject.
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(TOTALLY_UNDECLARED, 0.0, 0.0, 1.0);
        }
        """;

        ConvertResult r = Convert(glsl);
        r.Success.ShouldBeFalse();
        r.Diagnostics.ShouldContain(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("TOTALLY_UNDECLARED", StringComparison.Ordinal));
    }
}
