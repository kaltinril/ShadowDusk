#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// GitHub issue #308: a legacy <c>sampler</c> / <c>sampler2D</c> declared in an <c>#include</c>d
/// file, or whose register clause, whole declaration or <c>tex2D</c> read comes out of a macro,
/// did not compile on OpenGL (nor on Vulkan or DirectX 12): the pre-parser's SM4 rewrite read the
/// raw tokens of the main file only, so DXC was handed the legacy syntax and rejected it
/// ("unknown type name 'sampler2D'", "Unsupported intrinsic"). <c>mgfxc</c> 3.8.4.1 compiles
/// every shape here; every expected unit below was measured against it (<c>/Profile:OpenGL</c>,
/// 2026-10-02). The fix repeats the pre-parse on the preprocessed source, but only AFTER the
/// ordinary compile has failed, so every effect that compiled before is compiled from exactly the
/// same text (the corpus sweep measured 117 of 117 OpenGL and 125 of 125 DirectX_11 fixtures
/// byte-identical).
/// </summary>
[Trait("Category", "Integration")]
public sealed class Issue308LegacySamplerRecoveryTests
{
    private static CancellationTokenSource Cts() => new(TestBudget.Compile);

    private const string Header = """
        #if OPENGL
        #define PS_SHADERMODEL ps_3_0
        #else
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #endif

        """;

    private const string PixelShader = """

        float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S, uv); }
        technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
        """;

    private static IIncludeResolver Include(string text) =>
        new InMemoryIncludeResolver(new Dictionary<string, string> { ["s.fxh"] = text });

    private static async Task<Result<CompiledShader, ShaderError[]>> Compile(
        string source, PlatformTarget target = PlatformTarget.OpenGL, string? include = null)
    {
        using var cts = Cts();
        return await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = target,
            SourceFileName = "Issue308.fx",
            IncludeResolver = include is null ? null : Include(include),
        }, cts.Token);
    }

    private static async Task<MgfxBlobReader> CompileGl(string source, string? include = null)
    {
        var result = await Compile(source, PlatformTarget.OpenGL, include);
        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        return MgfxBlobReader.Parse(result.Value.Data);
    }

    private static List<MgfxSamplerRecord> PixelSamplers(MgfxBlobReader mgfx)
    {
        var ps = mgfx.Shaders.Single(s => !s.IsVertex);
        return mgfx.Samplers.Where(s => s.ShaderIndex == ps.Index).ToList();
    }

    private static void GlslShouldSample(MgfxBlobReader mgfx, string uniform)
    {
        string glsl = System.Text.Encoding.UTF8.GetString(mgfx.Shaders.Single(s => !s.IsVertex).Bytecode);
        glsl.ShouldContain($"uniform sampler2D {uniform};", Case.Sensitive);
    }

    // -------------------------------------------------------------------------
    // The nine measured shapes of the issue, plus the ones the fix was built against
    // -------------------------------------------------------------------------

    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("sampler S : register(s1);")]
    [InlineData("sampler2D S : register(s1);")]
    [InlineData("sampler S : register(s1) = sampler_state { Texture = <Tex>; };")]
    [InlineData("sampler2D S : register(s1) = sampler_state { Texture = <Tex>; };")]
    public async Task OpenGl_LegacySamplerDeclaredInAnInclude_CompilesAndPinsItsRegister(string declaration)
    {
        MgfxBlobReader mgfx = await CompileGl(Header + "Texture2D Tex;\n#include \"s.fxh\"" + PixelShader, declaration);

        MgfxSamplerRecord record = PixelSamplers(mgfx).Single();
        record.Name.ShouldBe("ps_s1", customMessage: "mgfxc emits ps_s1: the include's register(s1) pins the unit");
        record.TextureSlot.ShouldBe((byte)1);
        GlslShouldSample(mgfx, "ps_s1");
    }

    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("#define SLOT(n) : register(n)\nsampler S SLOT(s1);", "ps_s1")]
    [InlineData("#define SLOT(n) : register(n)\nsampler S SLOT(s1) = sampler_state { Texture = <Tex>; };", "ps_s1")]
    [InlineData("#define SLOT(n) : register(n)\nsampler2D S SLOT(s1);", "ps_s1")]
    [InlineData("#define SLOT(n) : register(n)\nsampler2D S SLOT(s1) = sampler_state { Texture = <Tex>; };", "ps_s1")]
    [InlineData("#define SREG(i) register(s##i)\nsampler2D S : SREG(1);", "ps_s1")]
    [InlineData("#define DECLARE_TEXTURE(Name, index) sampler2D Name : register(s##index)\nDECLARE_TEXTURE(S, 1);", "ps_s1")]
    [InlineData("#define DECL sampler S : register(s1)\nDECL;", "ps_s1")]
    [InlineData("#define SAMPLER_T sampler2D\nSAMPLER_T S : register(s1);", "ps_s1")]
    public async Task OpenGl_LegacySamplerDeclaredThroughAMacro_CompilesAndPinsItsRegister(string declaration, string expected)
    {
        MgfxBlobReader mgfx = await CompileGl(Header + "Texture2D Tex;\n" + declaration + PixelShader);

        MgfxSamplerRecord record = PixelSamplers(mgfx).Single();
        record.Name.ShouldBe(expected);
        GlslShouldSample(mgfx, expected);
    }

    /// <summary>
    /// MonoGame's own <c>Macros.fxh</c> idiom, declaration AND read through macros, with the
    /// macros themselves coming from an include: the shape the real third-party fixtures use.
    /// </summary>
    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_DeclareTextureAndSampleTextureMacrosFromAnInclude_Compile()
    {
        const string include = """
            #define DECLARE_TEXTURE(Name, index) \
                sampler2D Name : register(s##index);
            #define SAMPLE_TEXTURE(Name, texCoord)  tex2D(Name, texCoord)
            """;
        const string source = Header + """
            #include "s.fxh"
            DECLARE_TEXTURE(A, 2);
            DECLARE_TEXTURE(B, 1);
            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
            {
                return SAMPLE_TEXTURE(A, uv) + SAMPLE_TEXTURE(B, uv);
            }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        MgfxBlobReader mgfx = await CompileGl(source, include);

        string table = string.Join(" ", PixelSamplers(mgfx)
            .OrderBy(s => s.TextureSlot)
            .Select(s => $"{s.Name}={mgfx.Parameters[s.Parameter].Name}"));
        table.ShouldBe("ps_s1=B_SDTexture ps_s2=A_SDTexture", customMessage: "mgfxc: A on ps_s2, B on ps_s1");
    }

    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("sampler S : register(s1);\n#include \"s.fxh\"", "float4 Fetch(float2 uv) { return tex2D(S, uv); }", "ps_s1")]
    [InlineData("#include \"s.fxh\"", "sampler2D S : register(s1);\nfloat4 Fetch(float2 uv) { return tex2D(S, uv); }", "ps_s1")]
    [InlineData("#include \"s.fxh\"", "sampler2D S;\nfloat4 Fetch(float2 uv) { return tex2D(S, uv); }", "ps_s0")]
    [InlineData("#include \"s.fxh\"", "texture Tex;\nsampler2D S : register(s1) = sampler_state { Texture = (Tex); MinFilter = Linear; };\nfloat4 Fetch(float2 uv) { return tex2D(S, uv); }", "ps_s1")]
    public async Task OpenGl_Tex2DInsideTheInclude_IsRewrittenToo(string main, string include, string expected)
    {
        const string ps = """

            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return Fetch(uv); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        MgfxBlobReader mgfx = await CompileGl(Header + main + ps, include);

        PixelSamplers(mgfx).Single().Name.ShouldBe(expected);
        GlslShouldSample(mgfx, expected);
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_IncludeOnlyInTheOpenGlBranch_IsCompiledFromThatBranch()
    {
        const string source = Header + """
            #if OPENGL
            #include "s.fxh"
            #else
            Texture2D Tex; SamplerState S;
            #endif
            """ + PixelShader;

        MgfxBlobReader mgfx = await CompileGl(source, "sampler2D S : register(s1);");

        PixelSamplers(mgfx).Single().Name.ShouldBe("ps_s1");
    }

    /// <summary>
    /// An unused bare <c>sampler2D</c> is a declaration fxc drops and DXC has no type for. It
    /// stops the raw compile (issue #308's cousin), and the recovery turns it into an unused
    /// <c>SamplerState</c>. mgfxc: <c>ps_s0</c> for the one real sampler.
    /// </summary>
    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_UnusedBareSampler2D_NoLongerStopsTheCompile()
    {
        MgfxBlobReader mgfx = await CompileGl(Header + "sampler2D Unused;\nsampler2D S;" + PixelShader);

        PixelSamplers(mgfx).Single().Name.ShouldBe("ps_s0");
    }

    // -------------------------------------------------------------------------
    // The recovery is a RETRY: the same bytes as the directly written form
    // -------------------------------------------------------------------------

    /// <summary>
    /// The recovered compile must produce exactly what the directly written declaration
    /// produces, on every target that compiles through DXC. That is the proof the recovery
    /// changes nothing but the text the pre-parser could see: the rewritten source is the one the
    /// direct form always compiled from, and the direct form is rung-4 proven on all three.
    /// </summary>
    [Theory]
    [Trait("Platform", "OpenGL")]
    [Trait("Platform", "Vulkan")]
    [Trait("Platform", "DirectX12")]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.Vulkan)]
    [InlineData(PlatformTarget.DirectX12)]
    public async Task EveryDxcTarget_RecoveredCompile_IsByteIdenticalToTheDirectlyWrittenForm(PlatformTarget target)
    {
        const string direct = Header + "Texture2D Tex;\nsampler2D S : register(s1) = sampler_state { Texture = <Tex>; };" + PixelShader;
        const string viaInclude = Header + "Texture2D Tex;\n#include \"s.fxh\"" + PixelShader;
        const string viaMacro = Header + "Texture2D Tex;\n#define SLOT(n) : register(n)\nsampler2D S SLOT(s1) = sampler_state { Texture = <Tex>; };" + PixelShader;

        var expected = await Compile(direct, target);
        expected.IsSuccess.ShouldBeTrue(
            expected.IsFailure ? string.Join("; ", expected.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var include = await Compile(viaInclude, target, "sampler2D S : register(s1) = sampler_state { Texture = <Tex>; };");
        include.IsSuccess.ShouldBeTrue(
            include.IsFailure ? string.Join("; ", include.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        include.Value.Data.ShouldBe(expected.Value.Data,
            customMessage: $"{target}: the declaration in an include must compile to the bytes of the direct declaration");

        var macro = await Compile(viaMacro, target);
        macro.IsSuccess.ShouldBeTrue(
            macro.IsFailure ? string.Join("; ", macro.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        macro.Value.Data.ShouldBe(expected.Value.Data,
            customMessage: $"{target}: the macro-spelled declaration must compile to the bytes of the direct declaration");
    }

    /// <summary>
    /// DirectX 11 compiles legacy sampler syntax natively (vkd3d) and never takes the recovery:
    /// its output for the include shape is what it always was.
    /// </summary>
    [Fact]
    [Trait("Platform", "DirectX")]
    public async Task DirectX11_StillCompilesTheIncludeShape()
    {
        var result = await Compile(Header + "#include \"s.fxh\"" + PixelShader, PlatformTarget.DirectX, "sampler2D S : register(s1);");

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
    }

    // -------------------------------------------------------------------------
    // Diagnostics: the author's file and line, and a registered code for what is not modelled
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task AGenuineErrorInTheInclude_IsReportedInTheIncludeAtItsLine()
    {
        const string include = """
            sampler2D S : register(s1);

            float4 Broken(float2 uv) { return tex2D(S, uv) * undefined_symbol; }
            """;

        var result = await Compile(Header + "#include \"s.fxh\"" + PixelShader.Replace("tex2D(S, uv)", "Broken(uv)", StringComparison.Ordinal), include: include);

        result.IsFailure.ShouldBeTrue("the shader has a real error");
        ShaderError error = result.Error.First(e => e.Severity == ShaderErrorSeverity.Error);
        error.Message.ShouldContain("undefined_symbol", Case.Sensitive);
        error.File.ShouldEndWith("s.fxh");
        error.Line.ShouldBe(3);
        result.Error.ShouldNotContain(e => e.Code == "SD0016", customMessage: "everything legacy was rewritten; the error is the author's");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task ASampler2DFunctionParameter_FailsWithSd0016NamingTheShape()
    {
        const string include = """
            sampler2D S : register(s1);
            float4 Blur(sampler2D s, float2 uv) { return tex2D(s, uv); }
            """;

        var result = await Compile(Header + "#include \"s.fxh\"" + PixelShader.Replace("tex2D(S, uv)", "Blur(S, uv)", StringComparison.Ordinal), include: include);

        result.IsFailure.ShouldBeTrue();
        ShaderError residue = result.Error.Single(e => e.Code == "SD0016");
        residue.Message.ShouldContain("'sampler2D'", Case.Sensitive);
        residue.File.ShouldEndWith("s.fxh");
        residue.Line.ShouldBe(2);
        residue.Column.ShouldBe(13);
        result.Error[0].Code.ShouldNotBe("SD0016", customMessage: "the compiler's own diagnostic stays first");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task AConditionalOnACompilerPredefinedMacro_FailsWithSd0016_NotWithAGuess()
    {
        const string source = Header + """
            #if __HLSL_VERSION >= 2016
            #include "s.fxh"
            #else
            sampler S;
            #endif
            """ + PixelShader;

        var result = await Compile(source, include: "sampler2D S : register(s1);");

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.Single(e => e.Code == "SD0016");
        error.Message.ShouldContain("__HLSL_VERSION", Case.Sensitive);
        error.Line.ShouldBe(6);
        result.Error[0].Code.ShouldNotBe("SD0016", customMessage: "the compiler's own diagnostic stays first");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task ALegacyIntrinsicWithNoRewrite_HiddenInAMacro_IsItsOwnFx0012AtTheAuthorsLine()
    {
        const string source = Header + """
            #define SAMPLE_CUBE(s, uv) texCUBE(s, uv)
            samplerCUBE C : register(s1);
            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return SAMPLE_CUBE(C, float3(uv, 0)); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        var result = await Compile(source);

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("FX0012");
        result.Error.Single().File.ShouldBe("Issue308.fx");
        result.Error.Single().Line.ShouldBe(8);
        result.Error.Single().Message.ShouldContain("texCUBE", Case.Sensitive);
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task AnEffectWithNoLegacySyntax_KeepsItsOwnErrors_AndNoSd0016()
    {
        const string source = Header + """
            Texture2D Tex; SamplerState S;
            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return Tex.Sample(S, uv) * nope; }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        var result = await Compile(source);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotContain(e => e.Code == "SD0016");
        result.Error[0].Message.ShouldContain("nope", Case.Sensitive);
        result.Error[0].Line.ShouldBe(7);
    }

    // -------------------------------------------------------------------------
    // The real third-party effects the gap was blocking
    // -------------------------------------------------------------------------

    /// <summary>
    /// MonoGame's own test effects, vendored under <c>third-party/MonoGame</c>, all use
    /// <c>Include.fxh</c>'s DX9 branch on OpenGL (<c>DECLARE_TEXTURE</c> / <c>SAMPLE_TEXTURE</c>
    /// macros, <c>SV_TARGET0</c> as <c>COLOR0</c>, <c>ps_2_0</c>) and used to fail with
    /// "unknown type name 'sampler2D'". mgfxc 3.8.4.1 compiles them; the units are its.
    /// </summary>
    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("Bevels.fx", "ps_s0")]
    [InlineData("BlackOut.fx", "ps_s0")]
    [InlineData("ColorFlip.fx", "ps_s0")]
    [InlineData("CustomSpriteBatchEffect.fx", "ps_s0 ps_s1")]
    [InlineData("Grayscale.fx", "ps_s0")]
    [InlineData("HighContrast.fx", "ps_s0")]
    [InlineData("Invert.fx", "ps_s0")]
    [InlineData("NoEffect.fx", "ps_s0")]
    [InlineData("RainbowH.fx", "ps_s0")]
    public async Task OpenGl_MonoGamesOwnMacroLayerEffects_Compile(string fixture, string expectedUnits)
    {
        string path = TestHelpers.FixturePath("third-party/MonoGame/" + fixture);
        using var cts = Cts();
        var result = await new EffectCompiler().CompileAsync(await File.ReadAllTextAsync(path, cts.Token), new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName = path,
        }, cts.Token);
        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        MgfxBlobReader mgfx = MgfxBlobReader.Parse(result.Value.Data);
        string.Join(" ", PixelSamplers(mgfx).OrderBy(s => s.TextureSlot).Select(s => s.Name)).ShouldBe(expectedUnits);
    }
}
