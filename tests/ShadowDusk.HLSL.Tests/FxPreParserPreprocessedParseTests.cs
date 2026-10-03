#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// Issue #308: the pre-parse of the PREPROCESSED source (<see cref="FxPreParser.ParsePreprocessed"/>),
/// the recovery for a legacy sampler the raw pre-parse cannot see because an <c>#include</c> or a
/// macro supplies it, and the residue test that decides when it applies
/// (<see cref="FxPreParser.HasLegacySamplerResidue"/>). Pure: the "flattened" source is written by
/// hand with the <c>#line</c> directives the flattener would emit.
/// </summary>
public sealed class FxPreParserPreprocessedParseTests
{
    private const string Ps = """

        float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S, uv); }
        technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
        """;

    private const string Header = """
        #if OPENGL
        #define PS_SHADERMODEL ps_3_0
        #else
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #endif

        """;

    /// <summary>The flattened form of <paramref name="main"/>, with <paramref name="include"/> inlined where the main file says <c>#include "s.fxh"</c>.</summary>
    private static string Flattened(string main, string? include = null)
    {
        string prepend = PlatformMacros.For(PlatformTarget.OpenGL).ToTextPrepend("main.fx");
        if (include is null)
            return prepend + main;

        string[] lines = main.Split('\n');
        var sb = new System.Text.StringBuilder(prepend);
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "#include \"s.fxh\"")
            {
                sb.Append("#line 1 \"s.fxh\"\n").Append(include);
                if (!include.EndsWith('\n')) sb.Append('\n');
                sb.Append($"#line {i + 2} \"main.fx\"\n");
            }
            else
            {
                sb.Append(lines[i]).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static FxPreprocessedParse Parse(string flattened)
    {
        var result = FxPreParser.ParsePreprocessed(flattened, "main.fx");
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "ok");
        return result.Value;
    }

    private static string Stripped(string flattened) => Parse(flattened).Parsed.StrippedHlsl;

    // -------------------------------------------------------------------------
    // The shapes of issue #308, rewritten
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("sampler S : register(s1);")]
    [InlineData("sampler2D S : register(s1);")]
    [InlineData("sampler S : register(s1) = sampler_state { Texture = <Tex>; };")]
    [InlineData("sampler2D S : register(s1) = sampler_state { Texture = <Tex>; };")]
    public void ALegacySamplerDeclaredInAnInclude_IsRewritten(string declaration)
    {
        string stripped = Stripped(Flattened(Header + "Texture2D Tex;\n#include \"s.fxh\"" + Ps, declaration));

        stripped.ShouldNotContain("sampler2D", Case.Sensitive);
        stripped.ShouldNotContain("sampler_state", Case.Sensitive);
        stripped.ShouldNotContain("tex2D(", Case.Sensitive);
        stripped.ShouldContain("SamplerState S;", Case.Sensitive);
        stripped.ShouldContain(declaration.Contains("Texture = <Tex>", StringComparison.Ordinal)
            ? "Tex.Sample(S, uv)"
            : "S_SDTexture.Sample(S, uv)", Case.Sensitive);
    }

    [Theory]
    [InlineData("#define SLOT(n) : register(n)\nsampler S SLOT(s1);")]
    [InlineData("#define SLOT(n) : register(n)\nsampler2D S SLOT(s1) = sampler_state { Texture = <Tex>; };")]
    [InlineData("#define SREG(i) register(s##i)\nsampler2D S : SREG(1);")]
    [InlineData("#define DECLARE_TEXTURE(Name, index) sampler2D Name : register(s##index)\nDECLARE_TEXTURE(S, 1);")]
    [InlineData("#define DECL sampler S : register(s1)\nDECL;")]
    [InlineData("#define SAMPLER_T sampler2D\nSAMPLER_T S : register(s1);")]
    public void ALegacySamplerDeclaredThroughAMacro_IsRewritten(string declaration)
    {
        string stripped = Stripped(Flattened(Header + "Texture2D Tex;\n" + declaration + Ps));

        stripped.ShouldNotContain("sampler2D", Case.Sensitive);
        stripped.ShouldNotContain("tex2D(", Case.Sensitive);
        stripped.ShouldContain("SamplerState S;", Case.Sensitive);
    }

    [Fact]
    public void ATex2DInsideAMacroOrAnInclude_IsRewrittenToo()
    {
        const string main = Header + """
            #define SAMPLE_TEXTURE(Name, texCoord) tex2D(Name, texCoord)
            sampler2D S : register(s1);
            #include "s.fxh"
            float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return SAMPLE_TEXTURE(S, uv) + Fetch(uv); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        string stripped = Stripped(Flattened(main, "float4 Fetch(float2 uv) { return tex2D(S, uv); }"));

        stripped.ShouldNotContain("tex2D(", Case.Sensitive);
        string spaced = System.Text.RegularExpressions.Regex.Replace(stripped, @"\s+", " ");
        spaced.ShouldContain("return S_SDTexture.Sample(S, uv) + Fetch(uv);", Case.Sensitive);
        spaced.ShouldContain("float4 Fetch(float2 uv) { return S_SDTexture.Sample(S, uv); }", Case.Sensitive);
    }

    [Fact]
    public void OnlyTheActiveBranch_IsParsed()
    {
        const string main = Header + """
            #if OPENGL
            #include "s.fxh"
            #else
            Texture2D Tex; SamplerState S;
            #endif
            """ + Ps;

        FxPreprocessedParse parsed = Parse(Flattened(main, "sampler2D S : register(s1);"));

        parsed.Parsed.StrippedHlsl.ShouldNotContain("Texture2D Tex;", Case.Sensitive);
        parsed.Parsed.StrippedHlsl.ShouldContain("Texture2D S_SDTexture; SamplerState S;", Case.Sensitive);
        parsed.Parsed.Techniques.Single().Passes.Single().PixelProfile.ShouldBe("ps_3_0",
            customMessage: "the compile target is the literal the macro expanded to");
    }

    [Fact]
    public void AnUnusedBareLegacyTypedSampler_BecomesASamplerState()
    {
        // fxc accepts and drops it; DXC has no 'sampler2D' type. Its register still reserves,
        // but that is read off the view of the raw source, not off this text.
        string stripped = Stripped(Flattened(Header + "sampler2D Unused : register(s0);\nsamplerCUBE Cube;\nsampler2D S;" + Ps));

        stripped.ShouldContain("SamplerState Unused;", Case.Sensitive);
        stripped.ShouldContain("SamplerState Cube;", Case.Sensitive);
        stripped.ShouldNotContain("sampler2D", Case.Sensitive);
        stripped.ShouldNotContain("samplerCUBE", Case.Sensitive);
    }

    [Fact]
    public void ABareLegacyTypedSamplerReadThroughSample_IsLeftAlone_AndReportedAsResidue()
    {
        // mgfxc rejects 'sampler2D S' read through Tex.Sample (X3013), so it must not start
        // compiling here; the residue is what the caller turns into SD0016.
        const string main = Header + """
            Texture2D Tex;
            sampler2D S : register(s0);
            float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return Tex.Sample(S, uv); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        FxPreprocessedParse parsed = Parse(Flattened(main));

        parsed.Parsed.StrippedHlsl.ShouldContain("sampler2D S : register(s0);", Case.Sensitive);
        parsed.Residue.ShouldNotBeNull();
        parsed.Residue!.Token.ShouldBe("sampler2D");
        parsed.Residue.File.ShouldBe("main.fx");
        parsed.Residue.Line.ShouldBe(7);
    }

    [Fact]
    public void ASampler2DFunctionParameterInAnInclude_IsResidue_LocatedInTheInclude()
    {
        const string main = Header + "sampler2D S : register(s1);\n#include \"s.fxh\"" + Ps;
        const string include = """
            // helper
            float4 Blur(sampler2D s, float2 uv) { return tex2D(s, uv); }
            """;

        FxPreprocessedParse parsed = Parse(Flattened(main, include));

        parsed.Residue.ShouldNotBeNull();
        parsed.Residue!.Token.ShouldBe("sampler2D");
        parsed.Residue.File.ShouldBe("s.fxh");
        parsed.Residue.Line.ShouldBe(2);
        parsed.Residue.Column.ShouldBe(13);
    }

    [Fact]
    public void NoResidue_WhenEverythingWasRewritten()
    {
        Parse(Flattened(Header + "#include \"s.fxh\"" + Ps, "sampler2D S : register(s1);")).Residue.ShouldBeNull();
    }

    // -------------------------------------------------------------------------
    // Locations are the author's
    // -------------------------------------------------------------------------

    [Fact]
    public void Spans_AreMappedBackToTheAuthorsLines()
    {
        const string main = Header + """
            #include "s.fxh"
            Texture2D Tex;
            sampler2D S : register(s1) = sampler_state { Texture = <Tex>; MinFilter = Linear; };
            float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S, uv); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        FxParseResult parsed = Parse(Flattened(main, "float4 Pad;\nfloat4 Pad2;\nfloat4 Pad3;")).Parsed;

        // Header is 5 lines, the include directive is line 6, so the technique is on line 10 of
        // main.fx whatever the prepend and the inlined include did to the physical line count.
        parsed.Techniques.Single().Span.StartLine.ShouldBe(10);
        parsed.Techniques.Single().Passes.Single().PixelProfileSpan!.Value.StartLine.ShouldBe(10);
        parsed.Samplers.Single().Span.StartLine.ShouldBe(8);
        parsed.Samplers.Single().StateEntries.Single().Span.StartLine.ShouldBe(8);
    }

    [Fact]
    public void AnFxErrorOnThePreprocessedText_IsReportedAtTheAuthorsLocation()
    {
        // texCUBE has no 1:1 modern rewrite (FX0012); here it hides in a macro inside the include.
        const string main = Header + "#include \"s.fxh\"\nsamplerCUBE C : register(s1);\n" + """
            float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return SAMPLE_CUBE(C, uv); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
            """;

        var result = FxPreParser.ParsePreprocessed(Flattened(main, "\n#define SAMPLE_CUBE(s, uv) texCUBE(s, uv)"), "main.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("FX0012");
        result.Error.File.ShouldBe("main.fx");
        result.Error.Line.ShouldBe(8);
        result.Error.Message.ShouldContain("texCUBE", Case.Sensitive);
    }

    [Fact]
    public void AViewThatCannotBeBuilt_IsSd0009()
    {
        var result = FxPreParser.ParsePreprocessed("#if 1\nsampler2D S;\n", "main.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0009");
    }

    [Fact]
    public void AConditionalOnACompilerPredefinedMacro_IsReported()
    {
        const string main = Header + "#if __HLSL_VERSION >= 2021\nsampler2D S;\n#else\nsampler2D S : register(s1);\n#endif" + Ps;

        FxPreprocessedParse parsed = Parse(Flattened(main));

        parsed.CompilerPredefinedMacro.ShouldNotBeNull();
        parsed.CompilerPredefinedMacro!.Name.ShouldBe("__HLSL_VERSION");
        parsed.CompilerPredefinedMacro.File.ShouldBe("main.fx");
        parsed.CompilerPredefinedMacro.Line.ShouldBe(6);
    }

    // -------------------------------------------------------------------------
    // The residue test on the compiler's input
    // -------------------------------------------------------------------------

    private static bool Residue(string flattenedCompileInput)
    {
        var result = FxPreParser.HasLegacySamplerResidue(flattenedCompileInput, "main.fx");
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "ok");
        return result.Value.Found;
    }

    [Fact]
    public void ResidueCheck_ReportsAConditionalOnACompilerPredefinedMacro()
    {
        var result = FxPreParser.HasLegacySamplerResidue(
            Flattened(Header + "#if __HLSL_VERSION >= 2016\nsampler2D S;\n#else\nsampler S;\n#endif"), "main.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Found.ShouldBeFalse("the view took the #else branch");
        result.Value.CompilerPredefinedMacro.ShouldNotBeNull();
        result.Value.CompilerPredefinedMacro!.Name.ShouldBe("__HLSL_VERSION");
        result.Value.CompilerPredefinedMacro.Line.ShouldBe(6);
    }

    [Theory]
    [InlineData("sampler2D S;")]
    [InlineData("samplerCUBE S;")]
    [InlineData("sampler S = sampler_state { Texture = <Tex>; };")]
    [InlineData("#define F(s) tex2D(s, 0)\nfloat4 f() { return F(S); }")]
    [InlineData("float4 f() { return tex2Dlod(S, 0); }")]
    public void LegacySyntaxThatSurvivedTheRawRewrite_IsResidue(string text)
    {
        Residue(Flattened(Header + "Texture2D Tex; SamplerState S;\n" + text)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Texture2D S_SDTexture; SamplerState S;\nfloat4 f(float2 uv) { return S_SDTexture.Sample(S, uv); }")]
    [InlineData("sampler S;")]
    [InlineData("SamplerComparisonState C;")]
    [InlineData("#if 0\nsampler2D Dead;\n#endif")]
    [InlineData("// sampler2D in a comment\nfloat tex2D = 1;")]
    public void RewrittenOrModernSyntax_IsNotResidue(string text)
    {
        Residue(Flattened(Header + text)).ShouldBeFalse();
    }

    [Fact]
    public void MentionsLegacySamplerSyntax_SeesMacroBodiesAndDeadBranches()
    {
        FxPreParser.MentionsLegacySamplerSyntax("#define D(n) sampler2D n;\n").ShouldBeTrue();
        FxPreParser.MentionsLegacySamplerSyntax("#if 0\ntexCUBElod(s, c);\n#endif\n").ShouldBeTrue();
        FxPreParser.MentionsLegacySamplerSyntax("Texture2D T; SamplerState S; float4 f() { return T.Sample(S, 0); }").ShouldBeFalse();
        FxPreParser.MentionsLegacySamplerSyntax("float mysampler2D;").ShouldBeFalse();
    }
}
