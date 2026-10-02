#nullable enable

using ShadowDusk.Core;
using Shouldly;
using Xunit;
using Transport = ShadowDusk.Slang.Tests.SlangcTransport;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #302: the wiring that names a hoisted texture after the author's global, driven
/// through a FAKE slangc on both transports (the in-process seam the browser uses and the
/// process seam), so the browser route is pinned to the same behaviour as the desktop one on
/// every host with no disk and no process. The emissions and <c>-E</c> texts are slangc
/// v2026.14.1's, verbatim in shape. The mapping itself is <see cref="SlangcHoistedResourceNamesTests"/>;
/// the real-slangc proof is <see cref="SlangHoistedTextureNameTests"/>; what each shape costs in
/// slangc runs is also pinned by <see cref="SlangRegisterPassCostTests"/>.
/// </summary>
public sealed class SlangHoistedNamePassTests
{
    private const string PixelShader =
        "[shader(\"fragment\")]\nfloat4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return Comb.Sample(uv); }\n";

    private const string PixelShaderPreprocessed =
        "[ shader ( \"fragment\" ) ] float4 MainPS ( float2 uv : TEXCOORD0 ) : SV_Target { return Comb . Sample ( uv ) ; } \n";

    // slangc splits the combined sampler and locates the halves in its own core module.
    private const string CombinedEmission = """
        #pragma pack_matrix(column_major)

        #line 93 "core"
        Texture2D<float4 > Comb_texture_0 : register(t0);

        #line 1188 "hlsl.meta.slang"
        SamplerState Comb_sampler_0 : register(s0);

        #line 3 "<stdin>"
        float4 MainPS(float2 uv_0 : TEXCOORD0) : SV_TARGET
        {
            return Comb_texture_0.Sample(Comb_sampler_0, uv_0);
        }

        """;

    private static CompilerOptions Options(PlatformTarget target) =>
        new() { Target = target, SourceFileName = "Entry.slang" };

    [Theory]
    [InlineData(Transport.InProcess, PlatformTarget.DirectX)]
    [InlineData(Transport.InProcess, PlatformTarget.OpenGL)]
    [InlineData(Transport.Process, PlatformTarget.DirectX)]
    [InlineData(Transport.Process, PlatformTarget.OpenGL)]
    public void CombinedSampler_ReachesThePipelineUnderTheAuthorsName_WithNoExtraSlangcRun(
        Transport transport, PlatformTarget target)
    {
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(CombinedEmission, "Sampler2D Comb ; " + PixelShaderPreprocessed);

        var result = slangc.Compiler(transport, downstream).Compile("Sampler2D Comb;\n" + PixelShader, Options(target));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        string fx = downstream.Captured.ShouldNotBeNull();
        fx.ShouldContain("Texture2D<float4 > Comb;", Case.Sensitive);
        fx.ShouldContain("SamplerState Comb_sampler_0;", Case.Sensitive);
        fx.ShouldContain("return Comb.Sample(Comb_sampler_0, uv_0);", Case.Sensitive);
        fx.ShouldNotContain("Comb_texture_0", Case.Sensitive);
        // One compile and nothing else: the raw source decides.
        slangc.Calls.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void CombinedSamplerDeclaredThroughAMacro_AsksSlangcsPreprocessorOnce(Transport transport)
    {
        // The raw text never declares 'Comb' plainly, so only the preprocessed text can say
        // that 'Comb' is the global the halves were hoisted from.
        const string source = "#define DECLARE(n) Sampler2D n;\nDECLARE(Comb)\n" + PixelShader;
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(CombinedEmission, "Sampler2D Comb ; " + PixelShaderPreprocessed);

        var result = slangc.Compiler(transport, downstream).Compile(source, Options(PlatformTarget.DirectX));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        downstream.Captured.ShouldNotBeNull().ShouldContain("Texture2D<float4 > Comb;", Case.Sensitive);
        slangc.PreprocessInputs.ShouldBe([["-"]]);
        slangc.Calls.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void RegisterPassTexts_AreReused_NoSecondPreprocessRun(Transport transport)
    {
        // The register pass already preprocessed the entry source (the author wrote a register),
        // and the macro-declared global is decided from that same text.
        const string source = "#define DECLARE(n) Sampler2D n : register(t2);\nDECLARE(Comb)\n" + PixelShader;
        string emission = CombinedEmission.Replace("register(t0)", "register(t2)");
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(emission, "Sampler2D Comb : register ( t2 ) ; " + PixelShaderPreprocessed);

        var result = slangc.Compiler(transport, downstream).Compile(source, Options(PlatformTarget.DirectX));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        string fx = downstream.Captured.ShouldNotBeNull();
        fx.ShouldContain("Texture2D<float4 > Comb : register(t2);", Case.Sensitive);
        fx.ShouldContain("SamplerState Comb_sampler_0;", Case.Sensitive);
        slangc.PreprocessInputs.ShouldBe([["-"]]);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void BothEntryPoints_ShareOneRenamedDeclaration(Transport transport)
    {
        // Each entry point is its own slangc run and emits the split halves again; the merge
        // keeps one copy and the rename runs on the merged text.
        const string source =
            "Sampler2D Comb;\n" +
            "[shader(\"vertex\")]\nfloat4 MainVS(float2 uv : TEXCOORD0) : SV_Position { return Comb.SampleLevel(uv, 0); }\n" +
            PixelShader;
        string vertexEmission = CombinedEmission
            .Replace("float4 MainPS(float2 uv_0 : TEXCOORD0) : SV_TARGET", "float4 MainVS(float2 uv_0 : TEXCOORD0) : SV_POSITION")
            .Replace("Comb_texture_0.Sample(Comb_sampler_0, uv_0)", "Comb_texture_0.SampleLevel(Comb_sampler_0, uv_0, 0.0f)");
        var calls = new List<IReadOnlyList<string>>();
        (int, string, string) Run(string _, IReadOnlyList<string> arguments)
        {
            calls.Add(arguments);
            return (0, arguments.Contains("MainVS") ? vertexEmission : CombinedEmission, "");
        }
        var downstream = new ScriptedSlangc.CapturingCompiler();
        SlangCompiler compiler = transport == Transport.InProcess
            ? new SlangCompiler(downstream, Run)
            : new SlangCompiler(
                downstream,
                () => new SlangCompiler.SlangcLocation(null, ScriptedSlangc.FakePath),
                prepareSlangc: path => path,
                runSlangc: (_, _, text, arguments) => Run(text, arguments));

        var result = compiler.Compile(source, Options(PlatformTarget.DirectX));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        string fx = downstream.Captured.ShouldNotBeNull();
        fx.Split("Texture2D<float4 > Comb;").Length.ShouldBe(2, "one declaration of the renamed texture");
        fx.ShouldContain("return Comb.Sample(Comb_sampler_0, uv_0);", Case.Sensitive);
        fx.ShouldContain("return Comb.SampleLevel(Comb_sampler_0, uv_0, 0.0f);", Case.Sensitive);
        fx.ShouldNotContain("Comb_texture_0", Case.Sensitive);
        calls.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void FnaRespelling_DeclaresTheDx9TextureUnderTheAuthorsName(Transport transport)
    {
        // The rename runs before the DX9 respelling (issue #230), so the texture parameter FNA
        // binds by name is the author's as well.
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(CombinedEmission, "Sampler2D Comb ; " + PixelShaderPreprocessed);

        var result = slangc.Compiler(transport, downstream).Compile("Sampler2D Comb;\n" + PixelShader, Options(PlatformTarget.Fna));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        string fx = downstream.Captured.ShouldNotBeNull();
        fx.ShouldContain("texture2D Comb;", Case.Sensitive);
        fx.ShouldContain("sampler2D Comb_sampler_0 = sampler_state { Texture = <Comb>; };", Case.Sensitive);
        fx.ShouldNotContain("Comb_texture_0", Case.Sensitive);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void TextureHeldInAStruct_FailsAsSD0640_BeforeThePipeline(Transport transport)
    {
        const string source = """
            struct M { Texture2D t; SamplerState s; };
            M gM;
            [shader("fragment")]
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return gM.t.Sample(gM.s, uv); }
            """;
        const string emission = """
            #line 93 "core"
            Texture2D<float4 > gM_t_0 : register(t0);

            #line 1188 "hlsl.meta.slang"
            SamplerState gM_s_0 : register(s0);

            #line 4 "<stdin>"
            float4 MainPS(float2 uv_0 : TEXCOORD0) : SV_TARGET
            {
                return gM_t_0.Sample(gM_s_0, uv_0);
            }

            """;
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(emission, "unused");

        var result = slangc.Compiler(transport, downstream).Compile(source, Options(PlatformTarget.OpenGL));

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0640");
        (error.File, error.Line, error.Column).ShouldBe(("Entry.slang", 2, 3));
        downstream.Captured.ShouldBeNull();
        slangc.Calls.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(Transport.InProcess, "")]
    [InlineData(Transport.Process, "  \n")]
    // Truncated: the entry point the compile just found is not in the output.
    [InlineData(Transport.Process, "Sampler2D Comb ; \n")]
    public void PreprocessRunForNames_WithUnusableOutput_IsSD0629_NeverAGuess(Transport transport, string output)
    {
        const string source = "#define DECLARE(n) Sampler2D n;\nDECLARE(Comb)\n" + PixelShader;
        var downstream = new ScriptedSlangc.CapturingCompiler();
        var slangc = new ScriptedSlangc(CombinedEmission, output);

        var result = slangc.Compiler(transport, downstream).Compile(source, Options(PlatformTarget.DirectX));

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0629");
        error.Message.ShouldContain("which finds the names the author wrote", Case.Sensitive);
        error.Message.ShouldContain("will not guess", Case.Sensitive);
        downstream.Captured.ShouldBeNull();
    }
}
