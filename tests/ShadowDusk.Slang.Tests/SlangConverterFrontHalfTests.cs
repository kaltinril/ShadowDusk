#nullable enable

using ShadowDusk.Compiler.Raylib;
using ShadowDusk.Compiler.Sksl;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #253: <see cref="SlangCompiler.ConvertToSksl"/> and <see cref="SlangCompiler.ConvertToRaylib"/>
/// run the SAME real-slangc front half as <see cref="SlangCompiler.Compile"/>, not a copy of it.
/// Each test drives a check that lives only in that front half (the <c>.fx</c>-input guard
/// <c>SD0626</c>, the per-entry merge and its <c>SD0625</c>, the OpenGL declaration order) with
/// a fake slangc, so the proof holds on every OS without the native.
/// </summary>
public sealed class SlangConverterFrontHalfTests
{
    private const string FakeSlangcPath = "fake-tools/slangc";

    private static SlangCompiler.SlangcLocation MustNotBeReached() =>
        throw new InvalidOperationException("the slangc host/native lookup ran before the .fx-input rejection");

    private sealed class RecordingCompiler : IShaderCompiler
    {
        public string? CapturedFx;

        public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Compile(hlslSource, options, cancellationToken));

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Result<CompiledShader, ShaderError[]> Compile(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            CapturedFx = hlslSource;
            return Result<CompiledShader, ShaderError[]>.Ok(new CompiledShader(options.Target, [0x4D, 0x47, 0x46, 0x58]));
        }
    }

    // One canned emission per entry point, answered for the per-entry compile shape of
    // SlangcArguments.Build ('... -entry <name> -stage <stage> -- -'). Any other run fails.
    private static SlangCompiler Create(IReadOnlyDictionary<string, string> hlslByEntry, RecordingCompiler? downstream = null) =>
        new(downstream ?? new RecordingCompiler(),
            () => new SlangCompiler.SlangcLocation(null, FakeSlangcPath),
            prepareSlangc: path => path,
            runSlangc: (path, _, _, arguments) =>
            {
                path.ShouldBe(FakeSlangcPath);
                int entry = arguments.ToList().IndexOf("-entry");
                entry.ShouldBeGreaterThanOrEqualTo(0, "only per-entry compiles may run for these sources");
                return (0, hlslByEntry[arguments[entry + 1]], "");
            });

    private const string Prelude = """
        #pragma pack_matrix(column_major)
        #ifdef SLANG_HLSL_ENABLE_NVAPI
        #include "nvHLSLExtns.h"
        #endif

        """;

    // ---- SD0626: an HLSL Effect file is refused before slangc is looked for ----

    private const string EffectSource = """
        float4 MainPS(float2 uv : TEXCOORD0) : COLOR0 { return float4(uv, 0, 1); }
        technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
        """;

    [Fact]
    public void ConvertToSksl_AnFxFile_IsRefusedWithSD0626()
    {
        var result = new SlangCompiler(downstreamCompiler: null, MustNotBeReached)
            .ConvertToSksl(EffectSource, new SkslConvertOptions { SourceName = "effect.fx" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0626");
        result.Error.Single().File.ShouldBe("effect.fx");
    }

    [Fact]
    public void UnsetSourceName_NamesTheSlangSource_NotAnFxFile()
    {
        var compiler = new SlangCompiler(downstreamCompiler: null, MustNotBeReached);

        compiler.ConvertToSksl(EffectSource, new SkslConvertOptions()).Error.Single().File.ShouldBe("<memory>.slang");
        compiler.ConvertToRaylib(EffectSource, new RaylibConvertOptions()).Error.Single().File.ShouldBe("<memory>.slang");
    }

    [Fact]
    public void ConvertToRaylib_AnFxFile_IsRefusedWithSD0626()
    {
        var result = new SlangCompiler(downstreamCompiler: null, MustNotBeReached)
            .ConvertToRaylib(EffectSource, new RaylibConvertOptions { SourceName = "effect.fx" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0626");
    }

    // ---- SD0625: the per-entry merge runs before any converter rule ----

    private const string TwoEntrySource = """
        [shader("vertex")]
        float4 MainVS(float4 p : POSITION) : SV_Position { return p; }

        [shader("fragment")]
        float4 MainPS() : SV_Target { return 1.0; }
        """;

    private static readonly Dictionary<string, string> ConflictingEntries = new()
    {
        ["MainVS"] = Prelude + """
            #line 3
            cbuffer Params : register(b0)
            {
                float A;
            }

            #line 9
            float4 MainVS(float4 p_0 : POSITION) : SV_Position
            {
                return p_0 * A;
            }

            """,
        ["MainPS"] = Prelude + """
            #line 3
            cbuffer Params : register(b0)
            {
                float B;
            }

            #line 12
            float4 MainPS() : SV_TARGET
            {
                return B;
            }

            """,
    };

    [Fact]
    public void ConvertToSksl_EntriesThatDisagreeOnAGlobal_FailWithSD0625_BeforeTheVertexStageRefusal()
    {
        var result = Create(ConflictingEntries).ConvertToSksl(TwoEntrySource, new SkslConvertOptions { SourceName = "Conflict.slang" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0625");
        result.Error.Single().File.ShouldBe("Conflict.slang");
        result.Error.Single().Message.ShouldContain("'Params'", Case.Sensitive);
    }

    [Fact]
    public void ConvertToRaylib_EntriesThatDisagreeOnAGlobal_FailWithSD0625_BeforeTheVertexStageRefusal()
    {
        var result = Create(ConflictingEntries).ConvertToRaylib(TwoEntrySource, new RaylibConvertOptions { SourceName = "Conflict.slang" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0625");
    }

    // ---- The OpenGL declaration order reaches the raylib allocator ----

    // 'Base' is declared first and sampled second; slangc emits globals in first-use order, so
    // its emission (canned here, in that order) lists 'Mask' first.
    private const string TwoTextureSource = """
        Texture2D Base;
        SamplerState BaseSampler;
        Texture2D Mask;
        SamplerState MaskSampler;

        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            float4 m = Mask.Sample(MaskSampler, uv);
            float4 b = Base.Sample(BaseSampler, uv);
            return b * m;
        }
        """;

    private static readonly Dictionary<string, string> FirstUseOrderEmission = new()
    {
        ["MainPS"] = Prelude + """
            Texture2D<float4 > Mask;
            SamplerState MaskSampler;
            Texture2D<float4 > Base;
            SamplerState BaseSampler;
            float4 MainPS(float4 pos_0 : SV_Position, float2 uv_0 : TEXCOORD0) : SV_TARGET
            {
                float4 m_0 = Mask.Sample(MaskSampler, uv_0);
                float4 b_0 = Base.Sample(BaseSampler, uv_0);
                return b_0 * m_0;
            }

            """,
    };

    [Fact]
    public void ConvertToRaylib_BindsTheFirstDeclaredTexture_ToTexture0_LikeTheOpenGlCompile()
    {
        var result = Create(FirstUseOrderEmission).ConvertToRaylib(
            TwoTextureSource, new RaylibConvertOptions { SourceName = "TwoTextures.slang" });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        RaylibSampler drawTexture = result.Value.Samplers.Single(s => s.BoundByDrawCall);
        drawTexture.HlslTextureName.ShouldBe("Base");
        drawTexture.UniformName.ShouldBe("texture0");
    }

    [Fact]
    public void PositiveControl_TheSameAssembledFx_WithoutTheDeclarationOrder_BindsTheOtherTexture()
    {
        // Without the order the allocator follows the emission, so the test above can only pass
        // because the declaration order reached the converter.
        var downstream = new RecordingCompiler();
        Create(FirstUseOrderEmission, downstream)
            .Compile(TwoTextureSource, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "TwoTextures.slang" })
            .IsSuccess.ShouldBeTrue();
        string fx = downstream.CapturedFx.ShouldNotBeNull();

        var plain = RaylibConverter.Convert(fx, new RaylibConvertOptions { SourceName = "TwoTextures.slang" });

        plain.IsSuccess.ShouldBeTrue(plain.IsFailure ? string.Join(" | ", plain.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        plain.Value.Samplers.Single(s => s.BoundByDrawCall).HlslTextureName.ShouldBe("Mask");
    }

    // ---- A combined sampler's own register pins its unit in the raylib allocator ----

    private const string CombinedSamplerSource = """
        Sampler2D Tex : register(s1);

        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return Tex.Sample(uv);
        }
        """;

    // Real slangc v2026.14.1 output for CombinedSamplerSource with the OpenGL macros, captured
    // verbatim: the per-entry compile, which splits the combined sampler into two registered halves...
    private const string CombinedSamplerEmission = Prelude + """
        #ifndef __DXC_VERSION_MAJOR
        // warning X3557: loop doesn't seem to do anything, forcing loop to unroll
        #pragma warning(disable : 3557)
        #endif


        #line 93 "core"
        Texture2D<float4 > Tex_texture_0 : register(t0);


        #line 1188 "hlsl.meta.slang"
        SamplerState Tex_sampler_0 : register(s1);


        #line 4 "<stdin>"
        float4 MainPS(float4 pos_0 : SV_Position, float2 uv_0 : TEXCOORD0) : SV_TARGET
        {

        #line 4
            float2 _S1 = uv_0;

            ;

        #line 6
            return Tex_texture_0.Sample(Tex_sampler_0, _S1);
        }

        """;

    // ...and the preprocess-only pass the register pass reads to learn the author wrote register(s1).
    private const string CombinedSamplerPreprocessed =
        "Sampler2D Tex : register ( s1 ) ; [ shader ( \"fragment\" ) ] float4 MainPS ( float4 pos : SV_Position , " +
        "float2 uv : TEXCOORD0 ) : SV_Target { return Tex . Sample ( uv ) ; }\n";

    private static SlangCompiler CreateCombined(RecordingCompiler? downstream = null) =>
        new(downstream ?? new RecordingCompiler(),
            () => new SlangCompiler.SlangcLocation(null, FakeSlangcPath),
            prepareSlangc: path => path,
            runSlangc: (_, _, _, arguments) =>
                arguments.Contains("-entry") ? (0, CombinedSamplerEmission, "")
                : arguments.Contains("-E") ? (0, CombinedSamplerPreprocessed, "")
                : throw new InvalidOperationException("unexpected slangc run: " + string.Join(' ', arguments)));

    [Fact]
    public void ConvertToRaylib_ACombinedSamplerRegister_PinsItsUnit_LikeTheLegacySamplerOfAnFx()
    {
        var result = CreateCombined().ConvertToRaylib(
            CombinedSamplerSource, new RaylibConvertOptions { SourceName = "Combined.slang" });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        RaylibSampler sampler = result.Value.Samplers.Single();
        sampler.HlslTextureName.ShouldBe("Tex");
        // Unit 1, as mgfxc gives 'sampler2D Tex : register(s1)': not the draw call's texture0.
        sampler.BoundByDrawCall.ShouldBeFalse();
        sampler.UniformName.ShouldBe("Tex");

        const string legacyFx = """
            sampler2D Tex : register(s1);
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(Tex, uv); }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;
        var fx = RaylibConverter.Convert(legacyFx, new RaylibConvertOptions { SourceName = "Combined.fx" });
        fx.IsSuccess.ShouldBeTrue(fx.IsFailure ? string.Join(" | ", fx.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        fx.Value.Samplers.Single().BoundByDrawCall.ShouldBe(sampler.BoundByDrawCall);
    }

    [Fact]
    public void PositiveControl_TheSameAssembledFx_WithoutTheCombinedSamplerSlot_BindsItToTexture0()
    {
        // The front half takes the sampler register out of the HLSL and hands the unit over as
        // CombinedSamplerGlSlots; without that seam the converter sees no register and uses unit 0.
        var downstream = new RecordingCompiler();
        CreateCombined(downstream)
            .Compile(CombinedSamplerSource, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Combined.slang" })
            .IsSuccess.ShouldBeTrue();
        string fx = downstream.CapturedFx.ShouldNotBeNull();
        fx.ShouldNotContain("register(s1)", Case.Sensitive);

        var plain = RaylibConverter.Convert(fx, new RaylibConvertOptions { SourceName = "Combined.slang" });

        plain.IsSuccess.ShouldBeTrue(plain.IsFailure ? string.Join(" | ", plain.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        plain.Value.Samplers.Single().BoundByDrawCall.ShouldBeTrue();
    }
}
