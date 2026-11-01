#nullable enable
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// Issue #443: a function taking a legacy sampler parameter (<c>float4 F(sampler2D s, float2 uv)</c>)
/// is rewritten the way a legacy sampler declaration is: the parameter becomes
/// <c>Texture2D s_SDTexture, SamplerState s</c>, <c>tex2D(s, …)</c> in the body reads
/// <c>s_SDTexture</c>, and each call passes the sampler's texture beside it. Only shapes every use of
/// which the pre-scan follows; anything else is left verbatim, as it always was.
/// </summary>
public sealed class FxPreParserSamplerParameterTests
{
    private const string Technique = "\ntechnique T { pass P { PixelShader = compile ps_4_0 PS(); } }\n";

    private static FxParseResult Parse(string source)
    {
        var result = FxPreParser.Parse(source, sourceFile: "test.fx");
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "");
        return result.Value;
    }

    [Fact]
    public void ACalledSamplerParameter_IsRewrittenLikeADeclaration_WithItsCallsAndReads()
    {
        // K14: B is both passed and read directly; before, `F(B, uv)` handed the rewritten
        // SamplerState B to a sampler2D parameter (vkd3d E5005, two d3dcompiler records).
        FxParseResult parsed = Parse(
            "sampler2D A; sampler2D B;\n" +
            "float4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return F(B, uv) + tex2D(B, uv * 2) + tex2D(A, uv); }" + Technique);

        string hlsl = parsed.StrippedHlsl;
        hlsl.ShouldContain("Texture2D B_SDTexture; SamplerState B;", Case.Sensitive);
        hlsl.ShouldContain("float4 F(Texture2D s_SDTexture, SamplerState s, float2 uv) { return s_SDTexture.Sample(s, uv); }", Case.Sensitive);
        hlsl.ShouldContain("return F(B_SDTexture, B, uv) + B_SDTexture.Sample(B, uv * 2) + A_SDTexture.Sample(A, uv);", Case.Sensitive);
        parsed.SamplerParameterTextures["F"].ShouldBe(["s_SDTexture"]);
    }

    [Fact]
    public void ASamplerOnlyPassed_IsRewrittenToo()
    {
        // K13: B is never read with tex2D in the main body, only passed.
        string hlsl = Parse(
            "sampler2D A; sampler2D B;\n" +
            "float4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return F(B, uv) * 0.5 + tex2D(A, uv); }" + Technique).StrippedHlsl;

        hlsl.ShouldContain("Texture2D B_SDTexture; SamplerState B;", Case.Sensitive);
        hlsl.ShouldContain("F(B_SDTexture, B, uv) * 0.5", Case.Sensitive);
    }

    [Fact]
    public void AStateSamplerPassed_PassesTheTextureItsStateBlockNames()
    {
        string hlsl = Parse(
            "texture TexA;\nsampler SA = sampler_state { Texture = <TexA>; AddressU = Clamp; };\n" +
            "float4 F(sampler s, float2 uv) { return tex2D(s, uv); }\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return F(SA, uv); }" + Technique).StrippedHlsl;

        hlsl.ShouldContain("SamplerState SA;", Case.Sensitive);
        hlsl.ShouldContain("float4 F(Texture2D s_SDTexture, SamplerState s, float2 uv)", Case.Sensitive);
        hlsl.ShouldContain("return F(TexA, SA, uv);", Case.Sensitive);
    }

    [Fact]
    public void AParameterPassedOn_IsRewrittenInBothFunctions_AndAPrototypeToo()
    {
        FxParseResult parsed = Parse(
            "sampler2D B;\n" +
            "float4 G(in sampler2D t, float2 uv);\n" +
            "float4 F(float k, sampler2D s, float2 uv) { return G(s, uv) * k + tex2D(s, uv); }\n" +
            "float4 G(in sampler2D t, float2 uv) { return tex2Dgrad(t, uv, 0, 0); }\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return F(2, B, uv); }" + Technique);

        string hlsl = parsed.StrippedHlsl;
        hlsl.ShouldContain("float4 G(Texture2D t_SDTexture, SamplerState t, float2 uv);", Case.Sensitive);
        hlsl.ShouldContain("float4 F(float k, Texture2D s_SDTexture, SamplerState s, float2 uv) { return G(s_SDTexture, s, uv) * k + s_SDTexture.Sample(s, uv); }", Case.Sensitive);
        hlsl.ShouldContain("{ return t_SDTexture.SampleGrad(t, uv, 0, 0); }", Case.Sensitive);
        hlsl.ShouldContain("return F(2, B_SDTexture, B, uv);", Case.Sensitive);
        parsed.SamplerParameterTextures.Keys.Order(StringComparer.Ordinal).ShouldBe(["F", "G"]);
    }

    [Fact]
    public void ATex2DlodThroughAParameter_CallsTheHelperWithTheParametersTexture()
    {
        string hlsl = Parse(
            "sampler2D B;\n" +
            "float4 F(sampler2D s, float4 c) { return tex2Dlod(s, c); }\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return F(B, float4(uv, 0, 1)); }" + Technique).StrippedHlsl;

        hlsl.ShouldContain("float4 tex2Dlod_SD0(Texture2D sdTexture, SamplerState sdSampler, float4 sdCoord)", Case.Sensitive);
        hlsl.ShouldContain("return tex2Dlod_SD0(s_SDTexture, s, c);", Case.Sensitive);
    }

    [Fact]
    public void AMultiLineParameterList_KeepsEveryLine()
    {
        const string source =
            "sampler2D B;\n" +
            "float4 F(\n    sampler2D s,\n    float2 uv)\n{\n    return tex2D(s, uv);\n}\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return F(B, uv); }" + Technique;
        string hlsl = Parse(source).StrippedHlsl;

        hlsl.Split('\n').Length.ShouldBe(source.Split('\n').Length);
        hlsl.ShouldContain("    Texture2D s_SDTexture, SamplerState s,\n", Case.Sensitive);
    }

    // ---- Left verbatim: a use the pre-scan cannot see or follow --------------------------------

    [Theory]
    [InlineData("#define CALL(x) F(x, uv)\n", "return F(B, uv);")]                       // a macro may call it
    [InlineData("#define RD(x, c) tex2D(x, c)\n", "return F(B, uv) + RD(B, uv);")]           // B is used in a macro
    [InlineData("", "return F(B, uv) + Other(B, uv);")]                                    // B passed to a function left verbatim
    [InlineData("", "return F((B), uv);")]                                                 // not a plain name
    [InlineData("", "return F(B /* b */, uv);")]                                           // not a plain name
    [InlineData("", "float2 B2 = uv; return F(Undeclared, B2);")]                          // not a sampler the rewrite handles
    [InlineData("", "return F(B);")]                                                       // a default argument / wrong count
    public void AnUnfollowedUse_LeavesTheFunctionVerbatim(string prefix, string body)
    {
        string hlsl = Parse(
            prefix + "sampler2D B;\n" +
            "float4 Other(uniform sampler2D o, float2 uv) { return 0; }\n" /* left verbatim: a qualifier the rewrite does not follow */ +
            "float4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR0 { " + body + " }" + Technique).StrippedHlsl;

        hlsl.ShouldContain("float4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }", Case.Sensitive);
        hlsl.ShouldNotContain("_SDTexture,", Case.Sensitive);
    }

    [Theory]
    [InlineData("float4 F(sampler2D s, float2 uv) { float4 s2 = tex2D(s, uv); return s2 + s.x; }\n")]  // s used other than as a sampler argument
    [InlineData("float4 F(sampler2D s, float2 uv) { return Other(s, uv); }\n")]                         // passed to a function left verbatim
    [InlineData("float4 F(sampler2D s /* tex */, float2 uv) { return tex2D(s, uv); }\n")]               // a parameter the pattern does not match
    [InlineData("float4 F(uniform sampler2D s, float2 uv) { return tex2D(s, uv); }\n")]
    [InlineData("float4 F(samplerCUBE c, sampler2D s, float2 uv) { return tex2D(s, uv); }\n")]
    [InlineData("float4 F(sampler2D s, float2 uv) { float s_SDTexture = 1; return tex2D(s, uv) * s_SDTexture; }\n")]
    [InlineData("#if 1\nfloat4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }\n#else\nfloat4 F(sampler2D s, float2 uv) { return 0; }\n#endif\n")] // two definitions
    public void AnUnfollowedFunction_IsLeftVerbatim(string function)
    {
        string hlsl = Parse(
            "sampler2D B;\n" +
            "float4 Other(uniform sampler2D o, float2 uv) { return 0; }\n" /* left verbatim: a qualifier the rewrite does not follow */ + function +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return F(B, uv); }" + Technique).StrippedHlsl;

        hlsl.ShouldContain("F(B, uv)", Case.Sensitive);
        hlsl.ShouldNotContain("Texture2D s_SDTexture", Case.Sensitive);
    }

    [Fact]
    public void AnUnfollowedCallee_TakesTheFunctionThatPassesItsParameterWithIt()
    {
        // G's name is in a macro, so G stays verbatim; F passes its parameter to G, so F must stay
        // a sampler2D function too, or G would receive a SamplerState.
        string hlsl = Parse(
            "#define USE_G G\nsampler2D B;\n" +
            "float4 G(sampler2D t, float2 uv) { return tex2D(t, uv); }\n" +
            "float4 F(sampler2D s, float2 uv) { return G(s, uv); }\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return F(B, uv); }" + Technique).StrippedHlsl;

        hlsl.ShouldContain("float4 F(sampler2D s, float2 uv) { return G(s, uv); }", Case.Sensitive);
        hlsl.ShouldContain("return F(B, uv);", Case.Sensitive);
    }

    [Fact]
    public void ACallReachedBeforeItsSamplerIsBound_IsParsedAgainWithoutTheFunction()
    {
        // The pre-scan sees `sampler2D B;`, but only after the call; the main loop finds no binding
        // when it reaches the argument, so the parse is repeated with F left verbatim, never half
        // rewritten.
        FxParseResult parsed = Parse(
            "float4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return F(B, uv); }\n" +
            "sampler2D B;" + Technique);

        parsed.StrippedHlsl.ShouldContain("float4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }", Case.Sensitive);
        parsed.StrippedHlsl.ShouldContain("return F(B, uv);", Case.Sensitive);
        parsed.SamplerParameterTextures.ShouldBeEmpty();
    }

    [Fact]
    public void AnUncalledFunction_IsLeftVerbatim()
    {
        string hlsl = Parse(
            "sampler2D B;\n" +
            "float4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(B, uv); }" + Technique).StrippedHlsl;

        hlsl.ShouldContain("float4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }", Case.Sensitive);
    }

    [Fact]
    public void TheFnaMode_LeavesEverythingVerbatim()
    {
        const string source =
            "sampler2D B;\nfloat4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return F(B, uv); }" + Technique;
        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);
        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("float4 F(sampler2D s, float2 uv) { return tex2D(s, uv); }", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("return F(B, uv);", Case.Sensitive);
    }
}
