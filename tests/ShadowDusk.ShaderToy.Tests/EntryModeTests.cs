using FluentAssertions;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Unit coverage for the G2 plain-GLSL <c>void main()</c> entry mode: detection (mainImage vs main,
/// both = prefer ShaderToy + drop the main() wrapper with a Warning, neither = no-entry reject), the
/// <c>gl_FragColor</c> / user <c>out vec4</c> fragment-output bridging, the <c>gl_FragCoord</c> ->
/// harness pixel-coord mapping, and that the existing ShaderToy <c>mainImage</c> mode is untouched.
/// </summary>
public sealed class EntryModeTests
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

    // ── detection ─────────────────────────────────────────────────────────────

    [Fact]
    public void MainMode_Detected_AndWrapped()
    {
        const string glsl = """
        void main()
        {
            gl_FragColor = vec4(1.0, 0.0, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        // The translated entry is emitted as a `void main()` function and the PS calls it.
        r.Fx!.Should().Contain("void main()");
        r.Fx!.Should().Contain("main();", "the synthesized PS must invoke the plain-GLSL main()");
        // And NOT the ShaderToy mainImage wrapper.
        r.Fx!.Should().NotContain("mainImage(");
    }

    [Fact]
    public void ShaderToyMode_StillDetected_Unchanged()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        // ShaderToy mode keeps its existing harness: declares a local fragColor and calls mainImage.
        r.Fx!.Should().Contain("mainImage(fragColor, fragCoord);");
        r.Fx!.Should().NotContain("static float4 gl_FragCoord;");
    }

    // ── gl_FragColor as the PS return ─────────────────────────────────────────

    [Fact]
    public void GlFragColor_Write_BecomesPsReturn()
    {
        const string glsl = """
        void main()
        {
            gl_FragColor = vec4(0.2, 0.4, 0.6, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        // gl_FragColor is bridged as a static float4 the body writes and the PS returns.
        r.Fx!.Should().Contain("static float4 gl_FragColor;");
        r.Fx!.Should().Contain("return gl_FragColor;");
    }

    // ── user-declared out vec4 is consumed, becomes the return ────────────────

    [Fact]
    public void OutVar_IsConsumed_NotEmittedAsParameter_AndReturned()
    {
        const string glsl = """
        out vec4 myColor;
        void main()
        {
            myColor = vec4(1.0, 1.0, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        // The out var becomes the bridged output static + the PS return.
        r.Fx!.Should().Contain("static float4 myColor;");
        r.Fx!.Should().Contain("return myColor;");
        // It must NOT leak as a custom-uniform effect parameter or a top-level `out`/global decl.
        r.UsedUniforms.Should().NotContain("myColor");
        r.Fx!.Should().NotContain("out vec4 myColor");
        // The default gl_FragColor name is NOT used when a user output is declared.
        r.Fx!.Should().NotContain("return gl_FragColor;");
    }

    [Fact]
    public void LayoutLocation_OutVar_IsAccepted()
    {
        const string glsl = """
        layout(location = 0) out vec4 fragColor;
        void main()
        {
            fragColor = vec4(gl_FragCoord.xy / iResolution.xy, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        r.Fx!.Should().Contain("static float4 fragColor;");
        r.Fx!.Should().Contain("return fragColor;");
    }

    // ── gl_FragCoord resolves to the harness pixel coord ──────────────────────

    [Fact]
    public void GlFragCoord_ResolvesToHarnessPixelCoord_WithSameYFlip()
    {
        const string glsl = """
        void main()
        {
            gl_FragColor = vec4(gl_FragCoord.xy / iResolution.xy, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        // The body's gl_FragCoord.xy reference is emitted verbatim (bridged via the static global)...
        r.Fx!.Should().Contain("gl_FragCoord.xy");
        // ...and the PS computes it with the SAME bottom-left Y-flip the ShaderToy harness uses.
        r.Fx!.Should().Contain("float2 pixel = float2(input.UV.x, 1.0 - input.UV.y) * iResolution.xy;");
        r.Fx!.Should().Contain("gl_FragCoord = float4(pixel, 0.0, 1.0);");
    }

    // ── rejects ───────────────────────────────────────────────────────────────

    // ── both entries present → prefer ShaderToy, drop the main() wrapper with a Warning ──

    [Fact]
    public void BothEntries_PrefersShaderToy_DropsMainWrapper_WithWarning()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        void main()
        {
            mainImage(gl_FragColor, gl_FragCoord.xy);
        }
        """;

        ConvertResult r = ConvertOk(glsl);

        // ShaderToy mode is chosen: the harness wraps mainImage directly (its standard ShaderToy PS).
        r.Fx!.Should().Contain("mainImage(fragColor, fragCoord);");
        // The user `void main()` wrapper is dropped: it is NOT translated/emitted, and the plain-GLSL
        // bridging globals (gl_FragColor / gl_FragCoord statics) are NOT present in ShaderToy mode.
        r.Fx!.Should().NotContain("void main()");
        r.Fx!.Should().NotContain("static float4 gl_FragColor;");
        r.Fx!.Should().NotContain("static float4 gl_FragCoord;");
        // A Warning explains the wrapper was ignored in favor of mainImage.
        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("mainImage") && d.Message.Contains("main()"));
        // No errors.
        r.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void BothEntries_MainImageTranslation_IsIdenticalToWithoutWrapper()
    {
        const string body = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            fragColor = vec4(uv, 0.0, 1.0);
        }
        """;

        const string withWrapper = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            fragColor = vec4(uv, 0.0, 1.0);
        }
        void main()
        {
            mainImage(gl_FragColor, gl_FragCoord.xy);
        }
        """;

        ConvertResult plain = ConvertOk(body);
        ConvertResult both = ConvertOk(withWrapper);

        // Dropping the wrapper must leave the mainImage translation (and the whole harness) byte-identical.
        both.Fx.Should().Be(plain.Fx, "dropping the void main() wrapper must not change the mainImage output");
    }

    [Fact]
    public void BothEntries_SubstantiveMain_StillPrefersMainImage_AndDropsIt()
    {
        // A main() that does substantive work beyond calling mainImage is STILL dropped (mainImage is
        // canonical for a ShaderToy-derived file); we never try to merge them.
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        void main()
        {
            vec4 c;
            mainImage(c, gl_FragCoord.xy);
            gl_FragColor = c * 0.5 + vec4(0.1, 0.0, 0.0, 0.0);
        }
        """;

        ConvertResult r = ConvertOk(glsl);
        r.Fx!.Should().Contain("mainImage(fragColor, fragCoord);");
        r.Fx!.Should().NotContain("void main()");
        r.Diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Main_NoDiscoverableOutput_Rejected()
    {
        const string glsl = """
        void main()
        {
            vec2 uv = gl_FragCoord.xy / iResolution.xy;
            float x = length(uv);
        }
        """;

        ConvertResult r = Convert(glsl);
        r.Success.Should().BeFalse();
        r.Fx.Should().BeNull();
        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Message.Contains("fragment output"));
    }

    [Fact]
    public void NoEntryPoint_Rejected()
    {
        const string glsl = """
        float helper(float x) { return x * 2.0; }
        """;

        ConvertResult r = Convert(glsl);
        r.Success.Should().BeFalse();
        // With no `main` either, detection defaults to ShaderToy mode, whose validator reports the
        // missing mainImage entry. (A `main`-only shader with no output hits the main-mode reject above.)
        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("mainImage") && d.Message.Contains("found"));
    }

    [Fact]
    public void Main_WithParameters_Rejected()
    {
        // `void main(float x)` is not a valid plain-GLSL fragment entry.
        const string glsl = """
        void main(float x)
        {
            gl_FragColor = vec4(x);
        }
        """;

        ConvertResult r = Convert(glsl);
        r.Success.Should().BeFalse();
        r.Diagnostics.Should().Contain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Message.Contains("no parameters"));
    }
}
