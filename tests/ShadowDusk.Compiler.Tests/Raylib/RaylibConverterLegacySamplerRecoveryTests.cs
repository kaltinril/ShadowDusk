#nullable enable

using ShadowDusk.Compiler.Raylib;
using ShadowDusk.Compiler.Tests.Sksl;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests.Raylib;

/// <summary>
/// GitHub issue #327 (the converters' sibling of issue #308): a legacy <c>sampler</c> /
/// <c>sampler2D</c> declared in an <c>#include</c>d file, or whose register clause, whole
/// declaration or <c>tex2D</c> read comes out of a macro, did not convert. The raylib converter
/// pre-parses the raw main file and hands the seam its text, so DXC was given the legacy syntax
/// unrewritten and rejected it ("unknown type name 'sampler2D'", "deprecated tex2D intrinsic").
/// The fix is the same recovery <c>CompilationPipeline.Run</c> uses (<c>LegacySamplerRecovery</c>):
/// after the DXC compile has failed, the pre-parse is repeated on the preprocessed source and the
/// conversion runs again from it.
///
/// <para>raylib has no reference compiler, so the bar is the one the OpenGL route pins for the
/// same recovery: the include / macro form must convert to EXACTLY what the directly written
/// declaration converts to (fragment shader, uniforms, samplers and warnings alike), and nothing
/// that converted before may change (measured on the whole fixture corpus with the fix off and
/// on: 79 of 79 raylib conversions byte-identical).</para>
/// </summary>
public sealed class RaylibConverterLegacySamplerRecoveryTests
{
    private const string Header = """
        #if OPENGL
        #define PS_SHADERMODEL ps_3_0
        #else
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #endif

        """;

    private const string PixelShader = """

        float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S, uv) * color; }
        technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
        """;

    private static IIncludeResolver Include(string text) =>
        new InMemoryIncludeResolver(new Dictionary<string, string> { ["s.fxh"] = text });

    private static Result<RaylibShader, ShaderError[]> Convert(string fx, string? include = null) =>
        RaylibConverter.Convert(fx, new RaylibConvertOptions
        {
            SourceName = "Issue327.fx",
            IncludeResolver = include is null ? null : Include(include),
        });

    private static RaylibShader ConvertOk(string fx, string? include = null)
    {
        var result = Convert(fx, include);
        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code} {e.File}({e.Line}): {e.Message}")) : "");
        return result.Value;
    }

    /// <summary>The whole product, flattened to one comparable string: the bar is byte identity.</summary>
    private static string Flatten(RaylibShader shader) =>
        shader.FragmentShader + "\n" +
        string.Join("\n", shader.Uniforms.Select(u => $"{u.Name} {u.GlslType} {u.ArrayLength}")) + "\n" +
        string.Join("\n", shader.Samplers.Select(s =>
            $"{s.UniformName} {s.HlslTextureName} {s.HlslSamplerName} {s.BoundByDrawCall} " +
            string.Join(",", s.BakedSamplerState.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}")))) + "\n" +
        string.Join("\n", shader.Warnings.Select(w => $"{w.Code} {w.Line} {w.Message}"));

    private static void ShouldConvertLikeTheDirectForm(string direct, string recovered, string? include = null)
    {
        RaylibShader expected = ConvertOk(Header + direct + PixelShader);
        RaylibShader actual = ConvertOk(Header + recovered + PixelShader, include);

        Flatten(actual).ShouldBe(Flatten(expected),
            customMessage: "the recovered conversion must be exactly the directly written declaration's");
    }

    // -------------------------------------------------------------------------
    // Every measured shape converts, to exactly what its directly written twin converts to
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("sampler S : register(s1);")]
    [InlineData("sampler2D S : register(s1);")]
    [InlineData("sampler S : register(s1) = sampler_state { Texture = <Tex>; };")]
    [InlineData("sampler2D S : register(s1) = sampler_state { Texture = <Tex>; };")]
    public void LegacySamplerDeclaredInAnInclude_ConvertsLikeTheDirectDeclaration(string declaration)
    {
        ShouldConvertLikeTheDirectForm(
            direct: "Texture2D Tex;\n" + declaration,
            recovered: "Texture2D Tex;\n#include \"s.fxh\"",
            include: declaration);
    }

    [Theory]
    [InlineData("#define SLOT(n) : register(n)\nsampler S SLOT(s1);", "sampler S : register(s1);")]
    [InlineData("#define SLOT(n) : register(n)\nsampler2D S SLOT(s1) = sampler_state { Texture = <Tex>; };", "sampler2D S : register(s1) = sampler_state { Texture = <Tex>; };")]
    [InlineData("#define SREG(i) register(s##i)\nsampler2D S : SREG(1);", "sampler2D S : register(s1);")]
    [InlineData("#define DECLARE_TEXTURE(Name, index) sampler2D Name : register(s##index)\nDECLARE_TEXTURE(S, 1);", "sampler2D S : register(s1);")]
    [InlineData("#define DECL sampler S : register(s1)\nDECL;", "sampler S : register(s1);")]
    [InlineData("#define SAMPLER_T sampler2D\nSAMPLER_T S : register(s1);", "sampler2D S : register(s1);")]
    public void LegacySamplerDeclaredThroughAMacro_ConvertsLikeTheDirectDeclaration(string viaMacro, string direct)
    {
        ShouldConvertLikeTheDirectForm(
            direct: "Texture2D Tex;\n" + direct,
            recovered: "Texture2D Tex;\n" + viaMacro);
    }

    [Fact]
    public void Tex2DInsideAMacroBody_ConvertsLikeTheDirectCall()
    {
        const string body = """

            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return SAMPLE_TEXTURE(S, uv) * color; }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;
        RaylibShader expected = ConvertOk(Header + "sampler2D S : register(s1);" + PixelShader);
        RaylibShader actual = ConvertOk(Header + "sampler2D S : register(s1);\n#define SAMPLE_TEXTURE(Name, texCoord) tex2D(Name, texCoord)" + body);

        Flatten(actual).ShouldBe(Flatten(expected));
    }

    [Theory]
    [InlineData("sampler S : register(s1);\n#include \"s.fxh\"", "float4 Fetch(float2 uv) { return tex2D(S, uv); }", "sampler S : register(s1);\nfloat4 Fetch(float2 uv) { return tex2D(S, uv); }")]
    [InlineData("#include \"s.fxh\"", "sampler2D S : register(s1);\nfloat4 Fetch(float2 uv) { return tex2D(S, uv); }", "sampler2D S : register(s1);\nfloat4 Fetch(float2 uv) { return tex2D(S, uv); }")]
    [InlineData("#include \"s.fxh\"", "sampler2D S;\nfloat4 Fetch(float2 uv) { return tex2D(S, uv); }", "sampler2D S;\nfloat4 Fetch(float2 uv) { return tex2D(S, uv); }")]
    public void Tex2DInsideTheInclude_ConvertsLikeTheDirectForm(string main, string include, string direct)
    {
        const string body = """

            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return Fetch(uv) * color; }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;
        RaylibShader expected = ConvertOk(Header + direct + body);
        RaylibShader actual = ConvertOk(Header + main + body, include);

        Flatten(actual).ShouldBe(Flatten(expected));
    }

    /// <summary>
    /// MonoGame's own <c>Macros.fxh</c> idiom, declaration AND read through macros that come from
    /// an include, two textures on explicit registers: the registers decide which sampler is
    /// raylib's draw texture and which are bound by name, exactly as for the direct declarations.
    /// </summary>
    [Fact]
    public void DeclareTextureAndSampleTextureMacrosFromAnInclude_ConvertLikeTheDirectDeclarations()
    {
        const string include = """
            #define DECLARE_TEXTURE(Name, index) \
                sampler2D Name : register(s##index);
            #define SAMPLE_TEXTURE(Name, texCoord)  tex2D(Name, texCoord)
            """;
        const string viaMacros = Header + """
            #include "s.fxh"
            DECLARE_TEXTURE(A, 1);
            DECLARE_TEXTURE(B, 0);
            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
            {
                return SAMPLE_TEXTURE(A, uv) * SAMPLE_TEXTURE(B, uv) * color;
            }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;
        const string direct = Header + """
            sampler2D A : register(s1);
            sampler2D B : register(s0);
            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
            {
                return tex2D(A, uv) * tex2D(B, uv) * color;
            }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        RaylibShader expected = ConvertOk(direct);
        RaylibShader actual = ConvertOk(viaMacros, include);

        Flatten(actual).ShouldBe(Flatten(expected));
        // register(s0) is the unit the draw call binds, so B is texture0 even though A is declared first.
        actual.Samplers.Select(s => (s.UniformName, s.HlslTextureName, s.BoundByDrawCall)).ShouldBe(
            [("A_SDTexture", "A_SDTexture", false), ("texture0", "B_SDTexture", true)]);
    }

    [Fact]
    public void IncludeOnlyInTheOpenGlBranch_IsConvertedFromThatBranch()
    {
        const string source = Header + """
            Texture2D Tex;
            #if OPENGL
            #include "s.fxh"
            #else
            SamplerState S;
            #endif
            """ + PixelShader;

        ShouldConvertLikeTheDirectForm(
            direct: "Texture2D Tex;\nsampler2D S : register(s1);",
            recovered: source.Substring(Header.Length, source.Length - Header.Length - PixelShader.Length),
            include: "sampler2D S : register(s1);");
    }

    /// <summary>
    /// The baked <c>sampler_state</c> a consumer must apply by hand (<c>SD0637</c>) is read off
    /// the re-parse, so a block inside the include surfaces exactly as a block in the main file.
    /// </summary>
    [Fact]
    public void BakedSamplerStateInsideTheInclude_IsSurfacedLikeOneInTheMainFile()
    {
        const string declaration = "sampler2D S : register(s0) = sampler_state { Texture = <Tex>; Filter = Point; AddressU = Wrap; };";

        RaylibShader direct = ConvertOk(Header + "Texture2D Tex;\n" + declaration + PixelShader);
        RaylibShader recovered = ConvertOk(Header + "Texture2D Tex;\n#include \"s.fxh\"" + PixelShader, declaration);

        Flatten(recovered).ShouldBe(Flatten(direct));
        RaylibSampler sampler = recovered.Samplers.Single();
        sampler.BoundByDrawCall.ShouldBeTrue();
        sampler.BakedSamplerState["Filter"].ShouldBe("Point");
        sampler.BakedSamplerState["AddressU"].ShouldBe("Wrap");
        recovered.Warnings.Single().Code.ShouldBe("SD0637");
    }

    // -------------------------------------------------------------------------
    // The converter's own refusals still apply to a recovered effect
    // -------------------------------------------------------------------------

    [Fact]
    public void ARecoveredEffect_IsStillRefusedForWhatRaylibCannotHold()
    {
        // The include supplies the legacy sampler (so the recovery runs); the pixel shader reads
        // SV_Position, whose value differs between the two runtimes (SD0634). The refusal must
        // come out of the recovered pass exactly as it does for the direct declaration.
        const string body = """

            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S, uv) * pos.x; }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        var result = Convert(Header + "#include \"s.fxh\"" + body, "sampler2D S : register(s1);");

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0634");
    }

    // -------------------------------------------------------------------------
    // Diagnostics: the author's file and line, and SD0016 for what is not modelled
    // -------------------------------------------------------------------------

    [Fact]
    public void AGenuineErrorInTheInclude_IsReportedInTheIncludeAtItsLine_WithNoSd0016()
    {
        const string include = """
            sampler2D S : register(s1);

            float4 Broken(float2 uv) { return tex2D(S, uv) * undefined_symbol; }
            """;

        var result = Convert(Header + "#include \"s.fxh\"" + PixelShader.Replace("tex2D(S, uv)", "Broken(uv)", StringComparison.Ordinal), include);

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.First(e => e.Severity == ShaderErrorSeverity.Error);
        error.Message.ShouldContain("undefined_symbol", Case.Sensitive);
        error.File.ShouldEndWith("s.fxh");
        error.Line.ShouldBe(3);
        result.Error.ShouldNotContain(e => e.Code == "SD0016", customMessage: "everything legacy was rewritten; the error is the author's");
    }

    [Fact]
    public void ASampler2DFunctionParameter_FailsWithSd0016AfterTheCompilersOwnDiagnostic()
    {
        const string include = """
            sampler2D S : register(s1);
            float4 Blur(sampler2D s, float2 uv) { return tex2D(s, uv); }
            """;

        var result = Convert(Header + "#include \"s.fxh\"" + PixelShader.Replace("tex2D(S, uv)", "Blur(S, uv)", StringComparison.Ordinal), include);

        result.IsFailure.ShouldBeTrue();
        result.Error[0].Code.ShouldNotBe("SD0016", customMessage: "the compiler's own diagnostic stays first");
        ShaderError residue = result.Error.Single(e => e.Code == "SD0016");
        residue.Message.ShouldContain("'sampler2D'", Case.Sensitive);
        residue.File.ShouldEndWith("s.fxh");
        residue.Line.ShouldBe(2);
        residue.Column.ShouldBe(13);
    }

    [Fact]
    public void AConditionalOnACompilerPredefinedMacro_FailsWithSd0016_NotWithAGuess()
    {
        const string source = Header + """
            #if __HLSL_VERSION >= 2016
            #include "s.fxh"
            #else
            sampler S;
            #endif
            """ + PixelShader;

        var result = Convert(source, "sampler2D S : register(s1);");

        result.IsFailure.ShouldBeTrue();
        result.Error[0].Code.ShouldNotBe("SD0016");
        ShaderError error = result.Error.Single(e => e.Code == "SD0016");
        error.Message.ShouldContain("__HLSL_VERSION", Case.Sensitive);
        error.File.ShouldBe("Issue327.fx");
        error.Line.ShouldBe(6);
    }

    [Fact]
    public void AnEffectWithNoLegacySyntax_KeepsItsOwnError_AndNoSd0016()
    {
        const string source = Header + """
            Texture2D Tex; SamplerState S;
            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return Tex.Sample(S, uv) * nope; }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        var result = Convert(source);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotContain(e => e.Code == "SD0016");
        result.Error[0].Message.ShouldContain("nope", Case.Sensitive);
        result.Error[0].Line.ShouldBe(7);
    }

    // -------------------------------------------------------------------------
    // The real third-party effects the gap was blocking
    // -------------------------------------------------------------------------

    /// <summary>
    /// MonoGame's own test effects (vendored under <c>third-party/MonoGame</c>) declare and read
    /// their texture through <c>Include.fxh</c>'s <c>DECLARE_TEXTURE</c> / <c>SAMPLE_TEXTURE</c>
    /// macros. They used to be refused with DXC's "unknown type name 'sampler2D'"; the texture on
    /// register 0 is raylib's draw texture.
    /// </summary>
    [Theory]
    [InlineData("Bevels.fx")]
    [InlineData("BlackOut.fx")]
    [InlineData("ColorFlip.fx")]
    [InlineData("Grayscale.fx")]
    [InlineData("HighContrast.fx")]
    [InlineData("Invert.fx")]
    [InlineData("NoEffect.fx")]
    [InlineData("RainbowH.fx")]
    public void MonoGamesOwnMacroLayerEffects_Convert(string fixture)
    {
        string path = SkslConverterTests.FindFixture("third-party", "MonoGame", fixture);
        var result = RaylibConverter.Convert(File.ReadAllText(path), new RaylibConvertOptions { SourceName = path });

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        RaylibSampler draw = result.Value.Samplers.Single(s => s.BoundByDrawCall);
        draw.UniformName.ShouldBe("texture0");
        draw.HlslSamplerName.ShouldBe("s");
        result.Value.FragmentShader.ShouldContain("uniform sampler2D texture0;", Case.Sensitive);
    }

    [Fact]
    public void CustomSpriteBatchEffect_ConvertsWithItsSecondTextureBoundByName()
    {
        string path = SkslConverterTests.FindFixture("third-party", "MonoGame", "CustomSpriteBatchEffect.fx");
        var result = RaylibConverter.Convert(File.ReadAllText(path), new RaylibConvertOptions { SourceName = path });

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        result.Value.Samplers.Count.ShouldBe(2);
        result.Value.Samplers.Count(s => s.BoundByDrawCall).ShouldBe(1);
    }
}
