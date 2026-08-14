#nullable enable

using ShadowDusk.Compiler.Sksl;
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
    public void GumGrayscale_IsRejectedByDefault_BecauseItReadsColor0()
    {
        // THE Gum-lesson test: the one shader Gum actually ported by hand needed COLOR0
        // dropped to get there. Our default must be refusal, with the escape hatch named.
        var result = SkslConverter.Convert(File.ReadAllText(GumGrayscalePath),
            new SkslConvertOptions { SourceName = "Grayscale.fx" });

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.Single();
        error.Code.ShouldBe("SD0611");
        error.Message.ShouldContain("COLOR0", Case.Sensitive);
        error.Message.ShouldContain("TreatVaryingsAsUniforms", Case.Sensitive);
    }

    [Fact]
    public void GumGrayscale_ConvertsWithTheOptIn_KeepingTheTintTheHandPortDropped()
    {
        var result = SkslConverter.Convert(File.ReadAllText(GumGrayscalePath),
            new SkslConvertOptions
            {
                SourceName = "Grayscale.fx",
                TreatVaryingsAsUniforms = ["COLOR0"],
            });

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

        // The tint Gum's own hand port silently dropped is PRESENT, as the opted-into uniform,
        // and the contract surfaces it so the consumer knows to set it.
        sksl.ShouldContain("in_var_COLOR0", Case.Sensitive);
        result.Value.SynthesizedUniforms.ShouldContain("in_var_COLOR0");
        result.Value.Warnings.Single().Code.ShouldBe("SD0614");
        result.Value.ChildShaders.ShouldBe(["SpriteTexture"]);
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
    public void ComputedUvSampling_IsRejected_BecauseChildBoundsAreUnknowable()
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

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0612");
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

    /// <summary>Left↔right lerp between two uniform colors — used by the render test too.</summary>
    internal const string GradientFx = """
        float4 LeftColor;
        float4 RightColor;
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return lerp(LeftColor, RightColor, uv.x);
        }
        technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
        """;

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
