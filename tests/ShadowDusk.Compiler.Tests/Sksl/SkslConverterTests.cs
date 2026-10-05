#nullable enable

using ShadowDusk.Compiler.Sksl;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests.Sksl;

/// <summary>
/// The Phase 62 (issue #197) converter: HLSL <c>.fx</c> → SkSL text for
/// <c>SKRuntimeEffect</c>. Two groups here:
///
/// <para><b>Rejection tests</b> — the converter's most important behaviour. The convertible set
/// is fragment-only, coordinate-driven effects with uniform inputs; everything outside it must
/// be refused <b>loudly, by name</b>, because the known failure mode is real: Gum's own
/// hand-written SkSL port of this exact Grayscale silently drops the <c>* input.Color</c> tint
/// its <c>.fx</c> applies (Phase 62 §2.6). An automated tool that did the same would emit
/// SkSL that compiles and renders wrong.</para>
///
/// <para><b>Conversion + real-Skia evidence</b> — the owner-accepted bar (2026-08-13):
/// rendered-image fidelity, never <c>mgfxc</c>-equivalence (Skia has no reference compiler).
/// <see cref="SkslSkiaEvidenceTests"/> feeds the emission to real SkiaSharp — Skia's own
/// compiler is the acceptance check, and a CPU render against analytically computed pixels is
/// the fidelity check (tolerance ±2/255: SkSL evaluates at <c>half</c> precision).</para>
///
/// <para>These tests need DXC + SPIRV-Cross natives (always present in this suite's lanes) and
/// SkiaSharp as a <b>test-only</b> dependency — no product library references it.</para>
/// </summary>
public sealed class SkslConverterTests
{
    private static readonly string GumGrayscalePath = FindFixture(
        "third-party", "Gum", "MonoGameInCode-Grayscale.fx");

    [Fact]
    public void GumGrayscale_ConvertsByDefault_KeepingTheTintTheHandPortDropped()
    {
        // THE Gum-lesson test, issue #368 contract: COLOR0 (SpriteBatch's vertex color) is NOT
        // dropped (Gum's hand port did) and NOT refused: it becomes the synthesized uniform
        // ShadowDusk_Color, with no opt-in.
        var result = SkslConverter.Convert(File.ReadAllText(GumGrayscalePath),
            new SkslConvertOptions { SourceName = "Grayscale.fx" });

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");

        string sksl = result.Value.SkslText;

        // The SkSL conventions: child shader named after the HLSL texture, sampled with
        // .eval(coord); the fixed half4 main(float2) entry.
        sksl.ShouldContain("uniform shader SpriteTexture;", Case.Sensitive);
        sksl.ShouldContain("SpriteTexture.eval(coord)", Case.Sensitive);
        sksl.ShouldContain("half4 main(float2 coord)", Case.Sensitive);
        sksl.ShouldNotContain("texture(", Case.Sensitive);
        sksl.ShouldNotContain("gl_FragColor", Case.Sensitive);

        // The tint Gum's own hand port silently dropped is PRESENT, as the synthesized uniform,
        // and the contract surfaces it so the consumer knows to set it.
        sksl.ShouldContain("uniform vec4 ShadowDusk_Color;", Case.Sensitive);
        sksl.ShouldContain("* ShadowDusk_Color", Case.Sensitive);
        sksl.ShouldNotContain("in_var_COLOR0", Case.Sensitive);
        result.Value.SynthesizedUniforms.ShouldBe(["ShadowDusk_Color"]);
        var warning = result.Value.Warnings.Single();
        warning.Code.ShouldBe("SD0614");
        warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warning.Message.ShouldContain("ShadowDusk_Color", Case.Sensitive);
        result.Value.ChildShaders.ShouldBe(["SpriteTexture"]);
    }

    [Fact]
    public void ListingColor0InTreatVaryingsAsUniforms_IsAccepted_AndChangesNothing()
    {
        // Callers written before COLOR0 became the default still pass the option; they must
        // get the identical emission, not a second uniform name.
        string fx = File.ReadAllText(GumGrayscalePath);
        var byDefault = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = "Grayscale.fx" });
        var withOption = SkslConverter.Convert(fx, new SkslConvertOptions
        {
            SourceName = "Grayscale.fx",
            TreatVaryingsAsUniforms = ["COLOR0"],
        });

        byDefault.IsSuccess.ShouldBeTrue();
        withOption.IsSuccess.ShouldBeTrue();
        string.Equals(withOption.Value.SkslText, byDefault.Value.SkslText, StringComparison.Ordinal).ShouldBeTrue();
        withOption.Value.SynthesizedUniforms.ShouldBe(["ShadowDusk_Color"]);
        withOption.Value.Warnings.Count.ShouldBe(1);
    }

    private const string ExtraInterpolantFx = """
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0, float4 extra : TEXCOORD1) : SV_Target
        {
            return extra;
        }
        technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
        """;

    [Fact]
    public void AnInterpolantOtherThanTexcoord0AndColor0_IsStillRefusedByName()
    {
        var result = SkslConverter.Convert(ExtraInterpolantFx, new SkslConvertOptions { SourceName = "ti.fx" });

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.Single();
        error.Code.ShouldBe("SD0611");
        error.Message.ShouldContain("TEXCOORD1", Case.Sensitive);
        error.Message.ShouldContain("TreatVaryingsAsUniforms", Case.Sensitive);
    }

    [Fact]
    public void AnInterpolantOtherThanColor0_StillConvertsWithTheOptIn_AsInVarUniform()
    {
        var result = SkslConverter.Convert(ExtraInterpolantFx, new SkslConvertOptions
        {
            SourceName = "ti.fx",
            TreatVaryingsAsUniforms = ["TEXCOORD1"],
        });

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        result.Value.SkslText.ShouldContain("uniform vec4 in_var_TEXCOORD1;", Case.Sensitive);
        result.Value.SynthesizedUniforms.ShouldBe(["in_var_TEXCOORD1"]);
        result.Value.Warnings.Single().Code.ShouldBe("SD0614");
    }

    [Fact]
    public void BareColorSemantic_IsTheSameAsColor0()
    {
        // `: COLOR` with the index omitted IS COLOR0 (fxc/mgfxc rule); DXC passes the bare
        // spelling through as in_var_COLOR, which must not slip past the default.
        const string fx = """
            float4 MainPS(float4 pos : SV_Position, float4 c : COLOR) : SV_Target
            {
                return c;
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        var result = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = "bare.fx" });

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        result.Value.SkslText.ShouldContain("uniform vec4 ShadowDusk_Color;", Case.Sensitive);
        result.Value.SkslText.ShouldNotContain("in_var_COLOR", Case.Sensitive);
        result.Value.SynthesizedUniforms.ShouldBe(["ShadowDusk_Color"]);
    }

    [Fact]
    public void Color0ReadAsNarrowerThanFloat4_IsRefused_NotSilentlyWidened()
    {
        const string fx = """
            float4 MainPS(float4 pos : SV_Position, float3 c : COLOR0) : SV_Target
            {
                return float4(c, 1);
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        var result = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = "c3.fx" });

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.Single();
        error.Code.ShouldBe("SD0611");
        error.Message.ShouldContain("float4", Case.Sensitive);
    }

    [Fact]
    public void VertexShaderPass_IsRejected_NotSilentlyDropped()
    {
        const string fx = """
            struct V { float4 Position : SV_Position; };
            V MainVS(float4 p : POSITION) { V v; v.Position = p; return v; }
            float4 MainPS() : SV_Target { return float4(1, 0, 0, 1); }
            technique T { pass P {
                VertexShader = compile vs_3_0 MainVS();
                PixelShader = compile ps_3_0 MainPS();
            } }
            """;

        var result = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = "vs.fx" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0610");
        result.Error.Single().Message.ShouldContain("MainVS", Case.Sensitive);
    }

    [Fact]
    public void MultiPassEffect_IsRejected_RatherThanConvertingOnePassSilently()
    {
        const string fx = """
            float4 A() : SV_Target { return 1; }
            float4 B() : SV_Target { return 0; }
            technique T {
                pass P0 { PixelShader = compile ps_3_0 A(); }
                pass P1 { PixelShader = compile ps_3_0 B(); }
            }
            """;

        var result = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = "mp.fx" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0615");
    }

    [Fact]
    public void Derivatives_AreRejectedByName()
    {
        // fwidth has no SkSL runtime-effect equivalent; approximating it would render wrong.
        const string fx = """
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return float4(fwidth(uv), 0, 1);
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        var result = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = "fw.fx" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0613");
        result.Error.Single().Message.ShouldContain("fwidth", Case.Sensitive);
    }

    [Fact]
    public void ComputedUvSampling_Converts_ScaledByTheChildSizeUniform()
    {
        const string fx = """
            Texture2D Tex;
            sampler2D TexSampler = sampler_state { Texture = <Tex>; };
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return tex2D(TexSampler, uv * 2.0);
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        var result = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = "cs.fx" });

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        result.Value.SkslText.ShouldContain(".eval(", Case.Sensitive);
        result.Value.SkslText.ShouldContain("* ShadowDusk_Resolution)", Case.Sensitive);
        result.Value.SynthesizedUniforms.ShouldContain("ShadowDusk_Resolution");
    }

    [Fact]
    public void UniformDrivenGradient_Converts_WithASynthesizedResolutionUniform()
    {
        // The coordinate-driven no-texture case (§2.4's "gradient" band): arithmetic use of the
        // UV synthesizes ShadowDusk_Resolution, loudly.
        var result = SkslConverter.Convert(GradientFx, new SkslConvertOptions { SourceName = "grad.fx" });

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");

        result.Value.SkslText.ShouldContain("uniform vec2 ShadowDusk_Resolution;", Case.Sensitive);
        result.Value.SkslText.ShouldContain("coord / ShadowDusk_Resolution", Case.Sensitive);
        result.Value.SynthesizedUniforms.ShouldContain("ShadowDusk_Resolution");
        result.Value.Warnings.ShouldContain(w => w.Code == "SD0614");
        result.Value.ChildShaders.ShouldBeEmpty();
    }

    internal const string GradientFx = SkslTwinHarness.GradientFx;

    [Fact]
    public void InjectedBackends_AreUsed_AndProduceTheSameSkslAsTheDefaults()
    {
        // Issue #349: the WASM host injects its own DXC/SPIRV-Cross. Wrapping the desktop
        // ones proves the seam routes every call through the factories and that the output
        // is byte-identical to the default path.
        string fx = File.ReadAllText(GumGrayscalePath);
        var options = new SkslConvertOptions { SourceName = "Grayscale.fx" };
        int dxcCreated = 0, glslCreated = 0;

        var viaDefaults = SkslConverter.Convert(fx, options);
        var viaFactories = SkslConverter.Convert(fx, options,
            () => { dxcCreated++; return new ShadowDusk.HLSL.Dxc.DxcShaderCompiler(); },
            () => { glslCreated++; return new ShadowDusk.GLSL.SpirvCrossGlslTranspiler(); });

        viaFactories.IsSuccess.ShouldBeTrue();
        dxcCreated.ShouldBe(1);
        glslCreated.ShouldBe(1);
        string.Equals(viaFactories.Value.SkslText, viaDefaults.Value.SkslText, StringComparison.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void GumGrayscale_SkslMatchesTheCommittedGolden_TheBrowserGateComparesTheSameFile()
    {
        // Issue #349: tests/fixtures/golden/sksl/Grayscale.sksl is the desktop SkSL for the Gum
        // Grayscale (COLOR0 converts by default since #368). The browser gate (tests/ShadowDusk.BrowserTests/
        // browser-sksl-gate.mjs) asserts the in-browser conversion equals this same file, so
        // browser == golden == desktop. Regenerate with SHADOWDUSK_UPDATE_GOLDEN=1.
        string goldenPath = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(GumGrayscalePath)!)!)!)!,
            "golden", "sksl", "Grayscale.sksl");

        var result = SkslConverter.Convert(File.ReadAllText(GumGrayscalePath),
            new SkslConvertOptions { SourceName = "Grayscale.fx" });
        result.IsSuccess.ShouldBeTrue();

        if (Environment.GetEnvironmentVariable("SHADOWDUSK_UPDATE_GOLDEN") == "1")
            File.WriteAllText(goldenPath, result.Value.SkslText, new System.Text.UTF8Encoding(false));

        string golden = File.ReadAllText(goldenPath);
        string.Equals(result.Value.SkslText, golden, StringComparison.Ordinal).ShouldBeTrue();
    }

    internal static string FindFixture(params string[] parts)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(
                [dir.FullName, "tests", "fixtures", "shaders", .. parts]);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
