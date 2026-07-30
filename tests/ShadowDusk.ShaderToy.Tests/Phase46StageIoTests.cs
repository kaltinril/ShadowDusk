using Shouldly;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Unit coverage for the Phase 46 SECOND fixable batch (from the 160-shader failure analysis):
/// (1) ignored top-level stage I/O declarations (`in`/`varying`/`attribute`, incl. `layout(location)`)
/// with conventional coordinate varyings aliased to the harness screen UV; (2) OpenFL `#pragma header`
/// + `openfl_*` globals; (3) the Godot/GdShaders 4-arg `mainImage`; (4) the libretro VERTEX/FRAGMENT
/// stage split; (5) `switch` lowered to if/else, with fall-through staying a loud reject. Each asserts
/// the emitted HLSL for a hand-written minimal snippet (or a located reject when a value can't be known).
/// </summary>
public sealed class Phase46StageIoTests
{
    private static string ConvertOk(string glsl)
    {
        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.ShouldBeTrue(string.Format(
            "the shader is in-subset; diagnostics: {0}",
            string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"))));
        r.Fx.ShouldNotBeNull();
        return r.Fx!;
    }

    private static ConvertResult ConvertReject(string glsl)
    {
        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.ShouldBeFalse();
        r.Fx.ShouldBeNull();
        r.Diagnostics.ShouldContain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Line > 0 && d.Column > 0, "a reject must carry a located error");
        return r;
    }

    // ── 1: stage I/O declarations ignored; coordinate varyings alias the screen UV ──────────

    [Fact]
    public void TopLevelVarying_IsIgnored_NotAParameter()
    {
        const string glsl = """
        varying vec3 vNormalUnused;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.ShouldBeTrue();
        // The ignored varying is NOT emitted as a global/parameter and NOT a drivable uniform.
        r.Fx!.ShouldNotContain("vNormalUnused", Case.Sensitive);
        r.UsedUniforms.ShouldNotContain("vNormalUnused");
        // No error/warning is raised for the ignored declaration.
        r.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void CoordinateVarying_ReferencedAsUv_ResolvesToHarnessScreenUv()
    {
        const string glsl = """
        varying vec2 vUv;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(vUv, 0.5, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        // The varying reference resolves to the harness normalized screen UV static.
        fx.ShouldContain("static float2 sd_ScreenUV;", Case.Sensitive);
        fx.ShouldContain("sd_ScreenUV = fragCoord / iResolution.xy;", Case.Sensitive);
        fx.ShouldContain("fragColor = float4(sd_ScreenUV, 0.5, 1.0);", Case.Sensitive);
        fx.ShouldNotContain("vUv", Case.Sensitive);
    }

    [Fact]
    public void LayoutLocationIn_IsIgnored_AndCoordinateNameAliases()
    {
        const string glsl = """
        layout(location = 0) in vec2 texCoord;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(texCoord, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("static float2 sd_ScreenUV;", Case.Sensitive);
        fx.ShouldContain("float4(sd_ScreenUV, 0.0, 1.0)", Case.Sensitive);
        fx.ShouldNotContain("texCoord", Case.Sensitive);
    }

    [Fact]
    public void NonCoordinateVarying_Referenced_IsLoudUndeclaredReject()
    {
        const string glsl = """
        in vec3 vWorldNormal;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(vWorldNormal, 1.0);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d =>
            d.Message.Contains("Undeclared", StringComparison.Ordinal) &&
            d.Message.Contains("vWorldNormal", StringComparison.Ordinal));
    }

    // ── 2: OpenFL #pragma header + openfl_* globals ─────────────────────────────────────────

    [Fact]
    public void OpenFlHeader_PragmaStripped_AndGlobalsMapped()
    {
        const string glsl = """
        #pragma header
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = openfl_TextureCoordv;
            vec2 res = openfl_TextureSize;
            fragColor = vec4(uv, res.x / max(res.y, 1.0), 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldNotContain("#pragma header", Case.Sensitive);
        // openfl_TextureCoordv -> the harness screen UV static.
        fx.ShouldContain("static float2 sd_ScreenUV;", Case.Sensitive);
        fx.ShouldContain("float2 uv = sd_ScreenUV;", Case.Sensitive);
        // openfl_TextureSize -> iResolution.xy.
        fx.ShouldContain("float2 res = iResolution.xy;", Case.Sensitive);
        // The openfl_* identifiers are fully rewritten in the translated body (a comment in the harness
        // may still name openfl_TextureCoordv, so we assert on the assignment statements specifically).
        fx.ShouldNotContain("= openfl_", Case.Sensitive);
    }

    // ── 3: Godot / GdShaders 4-arg mainImage ────────────────────────────────────────────────

    [Fact]
    public void Godot4ArgMainImage_IsDetected_AndWired()
    {
        const string glsl = """
        void mainImage(in vec4 inputColor, in vec2 uv, out vec4 outputColor)
        {
            outputColor = vec4(inputColor.rgb * 0.5 + vec3(uv, 0.5) * 0.5, 1.0);
        }
        """;

        ConvertResult r = ShaderToyConverter.Convert(glsl);
        r.Success.ShouldBeTrue(string.Format(
            "diagnostics: {0}",
            string.Join("; ", r.Diagnostics.Select(d => $"{d.Severity}:{d.Message}"))));
        string fx = r.Fx!;
        // The harness derives Godot's SCREEN_UV, samples iChannel0 as inputColor, calls the 3-arg entry.
        fx.ShouldContain("float2 uv = fragCoord / iResolution.xy;", Case.Sensitive);
        fx.ShouldContain("float4 inputColor = tex2D(iChannel0, uv);", Case.Sensitive);
        fx.ShouldContain("mainImage(inputColor, uv, outputColor);", Case.Sensitive);
        fx.ShouldContain("return outputColor;", Case.Sensitive);
        // iChannel0 (the screen texture) is exposed as a drivable channel.
        fx.ShouldContain("sampler2D iChannel0", Case.Sensitive);
        r.UsedUniforms.ShouldContain("iChannel0");
    }

    [Fact]
    public void Godot4ArgMainImage_ConstInQualifiers_AlsoAccepted()
    {
        const string glsl = """
        void mainImage(const in vec4 inputColor, const in vec2 uv, out vec4 outputColor)
        {
            outputColor = vec4(uv, inputColor.b, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("mainImage(inputColor, uv, outputColor);", Case.Sensitive);
    }

    [Fact]
    public void StandardMainImage_StillTakesPrecedence_NotGodot()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        // The standard 2-arg harness call, NOT the Godot 3-arg one.
        fx.ShouldContain("mainImage(fragColor, fragCoord);", Case.Sensitive);
        fx.ShouldNotContain("float4 inputColor = tex2D", Case.Sensitive);
    }

    [Fact]
    public void ThreeArgMainImage_WrongShape_Rejects()
    {
        // 3 params but not the Godot shape (here all out) -> located reject, not silent.
        const string glsl = """
        void mainImage(out vec4 a, out vec2 b, out vec4 c)
        {
            a = vec4(1.0);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d => d.Message.Contains("Godot", StringComparison.Ordinal));
    }

    // ── 4: libretro VERTEX/FRAGMENT stage split ─────────────────────────────────────────────

    [Fact]
    public void LibretroVertexFragmentSplit_KeepsFragmentBranch()
    {
        const string glsl = """
        #if defined(VERTEX)
        attribute vec2 aPos;
        void vert_unused() { }
        #elif defined(FRAGMENT)
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.25, 1.0);
        }
        #endif
        """;

        string fx = ConvertOk(glsl);
        // The fragment branch (mainImage) survives; the vertex branch was stripped.
        fx.ShouldContain("mainImage(fragColor, fragCoord);", Case.Sensitive);
        fx.ShouldNotContain("vert_unused", Case.Sensitive);
        fx.ShouldNotContain("aPos", Case.Sensitive);
    }

    [Fact]
    public void LibretroIfdefStageSplit_KeepsFragmentBranch()
    {
        const string glsl = """
        #ifdef VERTEX
        void vert_unused() { }
        #endif
        #ifdef FRAGMENT
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(0.3, 0.6, 0.9, 1.0);
        }
        #endif
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("mainImage(fragColor, fragCoord);", Case.Sensitive);
        fx.ShouldNotContain("vert_unused", Case.Sensitive);
    }

    [Fact]
    public void NormalIfLogic_NotMisfiredByStageSplitHeuristic()
    {
        // A shader that uses an unrelated `#if` (only one of VERTEX/FRAGMENT, here neither) must NOT be
        // touched by the stage-split heuristic — it requires BOTH symbols to be guarded on.
        const string glsl = """
        #if 1
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(1.0, 0.0, 0.0, 1.0);
        }
        #endif
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("mainImage(fragColor, fragCoord);", Case.Sensitive);
    }

    // ── 5: switch lowered to if/else; fall-through rejects ───────────────────────────────────

    [Fact]
    public void Switch_LowersToIfElseChain()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            int band = int((fragCoord / iResolution.xy).x * 3.0);
            vec3 col;
            switch (band)
            {
                case 0: col = vec3(1.0, 0.0, 0.0); break;
                case 1: col = vec3(0.0, 1.0, 0.0); break;
                default: col = vec3(0.0, 0.0, 1.0); break;
            }
            fragColor = vec4(col, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        // The selector is hoisted once and the cases become an if / else if / else chain.
        fx.ShouldContain("int sd_sw0 = band;", Case.Sensitive);
        fx.ShouldContain("if (sd_sw0 == 0)", Case.Sensitive);
        fx.ShouldContain("else if (sd_sw0 == 1)", Case.Sensitive);
        fx.ShouldContain("else", Case.Sensitive);
        // No native switch / case / break (break outside a loop is illegal HLSL) leaks through.
        fx.ShouldNotContain("switch", Case.Sensitive);
        fx.ShouldNotContain("case ", Case.Sensitive);
    }

    [Fact]
    public void Switch_StackedLabels_ShareBody_AsOredCondition()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            int band = int((fragCoord / iResolution.xy).x * 4.0);
            vec3 col = vec3(0.0);
            switch (band)
            {
                case 1:
                case 2:
                    col = vec3(0.0, 1.0, 0.0);
                    break;
                default:
                    col = vec3(0.2, 0.2, 0.2);
                    break;
            }
            fragColor = vec4(col, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        // Stacked case 1 / case 2 share one body -> OR'd condition.
        fx.ShouldContain("if (sd_sw0 == 1 || sd_sw0 == 2)", Case.Sensitive);
    }

    [Fact]
    public void Switch_NoDefault_IsAllowed()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            int band = int((fragCoord / iResolution.xy).x * 2.0);
            vec3 col = vec3(0.5);
            switch (band)
            {
                case 0: col = vec3(1.0, 0.0, 0.0); break;
                case 1: col = vec3(0.0, 0.0, 1.0); break;
            }
            fragColor = vec4(col, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("if (sd_sw0 == 0)", Case.Sensitive);
        fx.ShouldContain("else if (sd_sw0 == 1)", Case.Sensitive);
    }

    [Fact]
    public void Switch_FallThrough_IsLoudReject()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            int band = int((fragCoord / iResolution.xy).x * 3.0);
            vec3 col = vec3(0.0);
            switch (band)
            {
                case 0:
                    col.r = 1.0;
                case 1:
                    col.g = 1.0;
                    break;
                default:
                    col.b = 1.0;
                    break;
            }
            fragColor = vec4(col, 1.0);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d => d.Message.Contains("fall-through", StringComparison.Ordinal));
    }
}
