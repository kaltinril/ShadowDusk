using Shouldly;
using Xunit;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Unit coverage for the Phase 46 FINAL coverage wave (from the 160-shader analysis): mainImage
/// prototype-vs-definition, the "returning" mainImage form, user-function overloading, macro / const-int
/// array sizes (incl. const-expressions), struct array members, single-argument matrix constructors
/// (diagonal scalar + matrix-from-matrix submatrix), sampler2D function parameters, the self-referential
/// macro C-rule, and the named out-of-scope rejects (cubemap / feedback / GL builtins / host template /
/// host-specific undeclared). Each asserts the emitted HLSL for a hand-written minimal snippet, or a
/// precisely-located reject for an out-of-scope construct.
/// </summary>
public sealed class Phase46CoverageWaveTests
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

    // ── 1: mainImage prototype + definition (not a duplicate) ───────────────────────────────

    [Fact]
    public void MainImagePrototype_ThenDefinition_ConvertsInShaderToyMode()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord);
        void main() { mainImage(gl_FragColor, gl_FragCoord.xy); }
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        // ShaderToy harness PS calls mainImage; the void main() wrapper is dropped (not emitted).
        fx.ShouldContain("mainImage(fragColor, fragCoord);", Case.Sensitive);
        fx.ShouldNotContain("void main(", Case.Sensitive);
    }

    [Fact]
    public void TwoTrueMainImageDefinitions_StillReject_AsConcatenatedMultipass()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord) { fragColor = vec4(1.0); }
        void mainImage(out vec4 fragColor, in vec2 fragCoord) { fragColor = vec4(0.0); }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d => d.Message.Contains("Multiple 'mainImage'", StringComparison.Ordinal));
    }

    // ── 2: the "returning" mainImage form (vec3/vec4 mainImage(vec2)) ───────────────────────

    [Fact]
    public void ReturningMainImage_Vec3_PadsToFloat4()
    {
        const string glsl = """
        vec3 mainImage(in vec2 fragCoord)
        {
            return vec3(fragCoord / iResolution.xy, 0.5);
        }
        void main() { gl_FragColor = vec4(mainImage(gl_FragCoord.xy), 1.0); }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("float3 rgb = mainImage(fragCoord);", Case.Sensitive);
        fx.ShouldContain("return float4(rgb, 1.0);", Case.Sensitive);
    }

    [Fact]
    public void ReturningMainImage_Vec4_ReturnedDirectly()
    {
        const string glsl = """
        vec4 mainImage(in vec2 fragCoord)
        {
            return vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        void main() { gl_FragColor = mainImage(gl_FragCoord.xy); }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("return mainImage(fragCoord);", Case.Sensitive);
    }

    // ── 3: user-function overloading ────────────────────────────────────────────────────────

    [Fact]
    public void SameNameHelpers_DifferentSignatures_BothEmitted()
    {
        const string glsl = """
        float f(float x) { return x * 2.0; }
        vec2 f(vec2 v) { return v * 3.0; }
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            fragColor = vec4(f(uv), f(uv.x), 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("float f(float x)", Case.Sensitive);
        fx.ShouldContain("float2 f(float2 v)", Case.Sensitive);
    }

    // ── 4: macro / const-int array sizes (incl. const-expressions) ──────────────────────────

    [Fact]
    public void ArraySize_FromDefine_ConstInt_AndConstExpression()
    {
        const string glsl = """
        #define KSIZE 4
        const int COUNT = 3;
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            float a[KSIZE];
            vec2 b[COUNT];
            float c[COUNT * 2];
            a[0] = 1.0; b[0] = vec2(0.0); c[0] = 1.0;
            fragColor = vec4(a[0], b[0].x, c[0], 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("float a[4];", Case.Sensitive);
        fx.ShouldContain("float2 b[3];", Case.Sensitive);
        fx.ShouldContain("float c[6];", Case.Sensitive);
    }

    [Fact]
    public void ArraySize_RuntimeNonConstant_StillRejects()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            int n = int(fragCoord.x);
            float a[n];
            fragColor = vec4(a[0], 0.0, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d => d.Message.Contains("constant integer", StringComparison.Ordinal));
    }

    // ── 5: struct array members ─────────────────────────────────────────────────────────────

    [Fact]
    public void StructArrayMember_EmittedWithSizeOnName_AndFactoryCopiesElementwise()
    {
        const string glsl = """
        struct Kernel { float w[4]; vec3 tint; };
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            Kernel k;
            k.w[0] = 0.1; k.w[1] = 0.2; k.w[2] = 0.3; k.w[3] = 0.4;
            k.tint = vec3(0.2, 0.6, 0.9);
            fragColor = vec4(k.tint * (k.w[0] + k.w[3]), 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("float w[4];", Case.Sensitive);
        // The factory copies the array element by element (no whole-array assignment in FX9/SM3).
        fx.ShouldContain("result.w[0] = w[0];", Case.Sensitive);
        fx.ShouldContain("result.w[3] = w[3];", Case.Sensitive);
    }

    // ── 6: single-argument matrix constructors ──────────────────────────────────────────────

    [Fact]
    public void MatrixFromScalar_ExpandsToDiagonalGrid()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            mat3 eye = mat3(1.0);
            vec3 v = vec3(fragCoord / iResolution.xy, 1.0);
            fragColor = vec4(eye * v, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("float3x3(1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0)", Case.Sensitive);
    }

    [Fact]
    public void MatrixFromMatrix_ExtractsUpperLeftSubmatrix()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            mat4 big = mat4(
                1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0,
                9.0, 10.0, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0);
            mat3 sub = mat3(big);
            vec3 v = vec3(fragCoord / iResolution.xy, 1.0);
            fragColor = vec4(sub * v, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        // Upper-left 3x3 submatrix read directly from the HLSL matrix components (the two transposes
        // cancel — see EmitMatrixFromMatrix).
        fx.ShouldContain("float3x3(big[0][0], big[0][1], big[0][2], big[1][0]", Case.Sensitive);
    }

    // ── 7: sampler2D function parameter (out of scope on GL/DX — named reject) ───────────────

    [Fact]
    public void Sampler2DParameter_RejectsByName_NotSilentlyWrong()
    {
        // A sampler2D parameter is valid HLSL but does NOT compile through the legacy-FX9 -> GL/DX
        // pipeline (a sampler cannot be a function argument there). Reject loudly at convert time rather
        // than emit GL/DX-incompatible output (same principle as the mip-bias texture reject).
        const string glsl = """
        vec4 sampleTex(sampler2D tex, vec2 uv) { return texture(tex, uv); }
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = sampleTex(iChannel0, fragCoord / iResolution.xy);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d =>
            d.Message.Contains("sampler2D", StringComparison.Ordinal) &&
            d.Message.Contains("function parameter", StringComparison.Ordinal));
    }

    // ── 8: self-referential macro C-rule ────────────────────────────────────────────────────

    [Fact]
    public void SelfReferentialMacro_Converts_NotRunaway()
    {
        const string glsl = """
        #define SCALE 2.0
        #define SCALE_PLUS (SCALE + 1.0)
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy * SCALE_PLUS, SCALE, 1.0);
        }
        """;

        string fx = ConvertOk(glsl);
        fx.ShouldContain("(2.0 + 1.0)", Case.Sensitive);
    }

    // ── 9: named out-of-scope rejects ───────────────────────────────────────────────────────

    [Fact]
    public void TextureCube_RejectsByName()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = textureCube(iChannel0, vec3(fragCoord / iResolution.xy, 1.0));
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d =>
            d.Message.Contains("textureCube", StringComparison.Ordinal) &&
            d.Message.Contains("CUBEMAP", StringComparison.Ordinal));
    }

    [Fact]
    public void GetLastFrameColor_RejectsAsFeedback()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = getLastFrameColor(fragCoord / iResolution.xy);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d => d.Message.Contains("feedback", StringComparison.Ordinal));
    }

    [Fact]
    public void GlFragDepth_RejectsByName_NotGenericUndeclared()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            gl_FragDepth = 0.5;
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d => d.Message.Contains("gl_FragDepth", StringComparison.Ordinal));
    }

    [Fact]
    public void HostSpecificUndeclared_RejectsAsHostProvidedValue()
    {
        const string glsl = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, iCurrentCursor.x, 1.0);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d =>
            d.Message.Contains("iCurrentCursor", StringComparison.Ordinal) &&
            d.Message.Contains("host-provided value", StringComparison.Ordinal));
    }

    [Fact]
    public void HostTemplatePlaceholder_RejectsByName()
    {
        const string glsl = """
        #define speed $speed
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy * speed, 0.0, 1.0);
        }
        """;

        ConvertResult r = ConvertReject(glsl);
        r.Diagnostics.ShouldContain(d => d.Message.Contains("host-template placeholder", StringComparison.Ordinal));
    }
}
