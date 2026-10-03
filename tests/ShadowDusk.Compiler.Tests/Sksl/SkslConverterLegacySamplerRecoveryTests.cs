#nullable enable

using ShadowDusk.Compiler.Sksl;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests.Sksl;

/// <summary>
/// GitHub issue #327 for the SkSL converter (the sibling of issue #308): a legacy
/// <c>sampler</c> / <c>sampler2D</c> declared in an <c>#include</c>d file, or whose register
/// clause, whole declaration or <c>tex2D</c> read comes out of a macro, did not convert, because
/// the converter pre-parses the raw main file and DXC was given the legacy syntax unrewritten.
/// The fix is the recovery <c>CompilationPipeline.Run</c> uses (<c>LegacySamplerRecovery</c>),
/// run after the DXC compile has failed.
///
/// <para>Skia has no reference compiler, so the bar is the one the OpenGL route pins for the
/// same recovery: the include / macro form converts to EXACTLY what the directly written
/// declaration converts to (SkSL text, children, synthesized uniforms and warnings), and nothing
/// that converted before changes (measured on the whole fixture corpus with the fix off and on:
/// 33 of 33 default and 69 of 69 opted-in SkSL conversions byte-identical).</para>
/// </summary>
public sealed class SkslConverterLegacySamplerRecoveryTests
{
    private const string Header = """
        #if OPENGL
        #define PS_SHADERMODEL ps_3_0
        #else
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #endif

        """;

    private const string PixelShader = """

        float4 PS(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S, uv); }
        technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
        """;

    private static IIncludeResolver Include(string text) =>
        new InMemoryIncludeResolver(new Dictionary<string, string> { ["s.fxh"] = text });

    private static Result<SkslConversion, ShaderError[]> Convert(
        string fx, string? include = null, IReadOnlyList<string>? varyingsAsUniforms = null) =>
        SkslConverter.Convert(fx, new SkslConvertOptions
        {
            SourceName = "Issue327.fx",
            IncludeResolver = include is null ? null : Include(include),
            TreatVaryingsAsUniforms = varyingsAsUniforms ?? [],
        });

    private static SkslConversion ConvertOk(string fx, string? include = null, IReadOnlyList<string>? varyingsAsUniforms = null)
    {
        var result = Convert(fx, include, varyingsAsUniforms);
        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code} {e.File}({e.Line}): {e.Message}")) : "");
        return result.Value;
    }

    /// <summary>The whole product, flattened to one comparable string: the bar is byte identity.</summary>
    private static string Flatten(SkslConversion conversion) =>
        conversion.SkslText + "\n" +
        string.Join("\n", conversion.ChildShaders) + "\n" +
        string.Join("\n", conversion.SynthesizedUniforms) + "\n" +
        string.Join("\n", conversion.Warnings.Select(w => $"{w.Code} {w.Line} {w.Message}"));

    private static void ShouldConvertLikeTheDirectForm(string direct, string recovered, string? include = null)
    {
        SkslConversion expected = ConvertOk(Header + direct + PixelShader);
        SkslConversion actual = ConvertOk(Header + recovered + PixelShader, include);

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

    [Theory]
    [InlineData("sampler S : register(s1);\n#include \"s.fxh\"", "float4 Fetch(float2 uv) { return tex2D(S, uv); }", "sampler S : register(s1);\nfloat4 Fetch(float2 uv) { return tex2D(S, uv); }")]
    [InlineData("#include \"s.fxh\"", "sampler2D S : register(s1);\nfloat4 Fetch(float2 uv) { return tex2D(S, uv); }", "sampler2D S : register(s1);\nfloat4 Fetch(float2 uv) { return tex2D(S, uv); }")]
    [InlineData("#include \"s.fxh\"", "sampler2D S;\nfloat4 Fetch(float2 uv) { return tex2D(S, uv); }", "sampler2D S;\nfloat4 Fetch(float2 uv) { return tex2D(S, uv); }")]
    public void Tex2DInsideTheInclude_ConvertsLikeTheDirectForm(string main, string include, string direct)
    {
        const string body = """

            float4 PS(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : COLOR0 { return Fetch(uv); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;
        SkslConversion expected = ConvertOk(Header + direct + body);
        SkslConversion actual = ConvertOk(Header + main + body, include);

        Flatten(actual).ShouldBe(Flatten(expected));
    }

    /// <summary>
    /// MonoGame's own <c>Macros.fxh</c> idiom, declaration AND read through macros from an
    /// include, two textures: both become <c>uniform shader</c> children named after the
    /// textures, in the direct declarations' order.
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
            float4 PS(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : COLOR0
            {
                return SAMPLE_TEXTURE(A, uv) * SAMPLE_TEXTURE(B, uv);
            }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;
        const string direct = Header + """
            sampler2D A : register(s1);
            sampler2D B : register(s0);
            float4 PS(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : COLOR0
            {
                return tex2D(A, uv) * tex2D(B, uv);
            }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        SkslConversion expected = ConvertOk(direct);
        SkslConversion actual = ConvertOk(viaMacros, include);

        Flatten(actual).ShouldBe(Flatten(expected));
        actual.ChildShaders.ShouldBe(["A_SDTexture", "B_SDTexture"]);
        actual.SkslText.ShouldContain("A_SDTexture.eval(coord)", Case.Sensitive);
    }

    /// <summary>
    /// The opt-in that turns an interpolant into a uniform (<c>TreatVaryingsAsUniforms</c>) is a
    /// mapper decision after the seam, so it must apply to a recovered effect exactly as to the
    /// direct one: refused by default (<c>SD0611</c>), converted with the opt-in.
    /// </summary>
    [Fact]
    public void ARecoveredEffectReadingAnExtraInterpolant_IsRefusedByDefault_AndConvertsWithTheOptInLikeTheDirectForm()
    {
        const string body = """

            float4 PS(float4 pos : SV_POSITION, float4 extra : TEXCOORD1, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S, uv) * extra; }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;
        const string include = "sampler2D S : register(s0);";

        var refused = Convert(Header + "#include \"s.fxh\"" + body, include);
        refused.IsFailure.ShouldBeTrue();
        refused.Error.Single().Code.ShouldBe("SD0611");

        SkslConversion direct = ConvertOk(Header + include + body, varyingsAsUniforms: ["TEXCOORD1"]);
        SkslConversion recovered = ConvertOk(Header + "#include \"s.fxh\"" + body, include, ["TEXCOORD1"]);
        Flatten(recovered).ShouldBe(Flatten(direct));
        recovered.SynthesizedUniforms.ShouldContain("in_var_TEXCOORD1");
    }

    /// <summary>
    /// COLOR0 converts by default (issue #368), and that default is a mapper decision after the
    /// seam, so a recovered effect gets it exactly as the direct form does.
    /// </summary>
    [Fact]
    public void ARecoveredEffectReadingColor0_ConvertsByDefault_LikeTheDirectForm()
    {
        const string body = """

            float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S, uv) * color; }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;
        const string include = "sampler2D S : register(s0);";

        SkslConversion direct = ConvertOk(Header + include + body);
        SkslConversion recovered = ConvertOk(Header + "#include \"s.fxh\"" + body, include);
        Flatten(recovered).ShouldBe(Flatten(direct));
        recovered.SynthesizedUniforms.ShouldContain("ShadowDusk_Color");
    }

    [Fact]
    public void ARecoveredEffect_IsStillRefusedForWhatSkslCannotHold()
    {
        // The include supplies the legacy sampler (so the recovery runs); the sampling coordinate
        // is computed, which SkSL's child-space .eval() cannot honour (SD0612). The refusal must
        // come out of the recovered pass exactly as it does for the direct declaration.
        const string body = """

            float4 PS(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S, uv * 2.0); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        var result = Convert(Header + "#include \"s.fxh\"" + body, "sampler2D S : register(s0);");

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0612");
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
        result.Error.ShouldNotContain(e => e.Code == "SD0016");
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
            float4 PS(float4 pos : SV_POSITION, float2 uv : TEXCOORD0) : COLOR0 { return Tex.Sample(S, uv) * nope; }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        var result = Convert(source);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotContain(e => e.Code == "SD0016");
        result.Error[0].Message.ShouldContain("nope", Case.Sensitive);
        result.Error[0].Line.ShouldBe(7);
    }

    // -------------------------------------------------------------------------
    // The real third-party effect the gap was blocking
    // -------------------------------------------------------------------------

    /// <summary>
    /// MonoGame's own Grayscale test effect declares and reads its texture through
    /// <c>Include.fxh</c>'s <c>DECLARE_TEXTURE</c> / <c>SAMPLE_TEXTURE</c> macros and samples at
    /// the interpolated coordinate, so it is inside SkSL's convertible set; it used to be refused
    /// with DXC's "unknown type name 'sampler2D'".
    /// </summary>
    [Fact]
    public void MonoGamesOwnGrayscale_Converts()
    {
        string path = SkslConverterTests.FindFixture("third-party", "MonoGame", "Grayscale.fx");
        var result = SkslConverter.Convert(File.ReadAllText(path), new SkslConvertOptions { SourceName = path });

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        result.Value.ChildShaders.ShouldBe(["s_SDTexture"]);
        result.Value.SkslText.ShouldContain("uniform shader s_SDTexture;", Case.Sensitive);
        result.Value.SkslText.ShouldContain("s_SDTexture.eval(coord)", Case.Sensitive);
    }
}
