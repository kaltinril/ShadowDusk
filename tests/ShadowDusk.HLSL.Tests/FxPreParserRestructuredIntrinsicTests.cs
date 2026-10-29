#nullable enable
using ShadowDusk.HLSL;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

// -----------------------------------------------------------------------------
// tex2Dlod / tex2Dbias / tex2Dproj in RewriteToSm4 mode. The coordinate's w carries
// the LOD, the bias or the projective divisor, so the call cannot be a plain name
// swap like tex2D's. Each becomes a call of a helper the pre-parser defines at global
// scope right before the declaration that calls it:
//   tex2Dlod(s, t)  ->  tex2Dlod_SD0(T, s, t)
//   float4 tex2Dlod_SD0(Texture2D sdTexture, SamplerState sdSampler, float4 sdCoord)
//   { return sdTexture.SampleLevel(sdSampler, sdCoord.xy, sdCoord.w); }
// so both arguments reach the compiler verbatim and are evaluated once.
// mgfxc 3.8.4.1 /Profile:DirectX_11 compiles all three (measured 2026-10-28).
// -----------------------------------------------------------------------------

public sealed class FxPreParserRestructuredIntrinsicTests
{
    private const string Technique = "\ntechnique T { pass P { PixelShader = compile ps_4_0 PSMain(); } }\n";

    private static string Rewrite(string source)
    {
        var result = FxPreParser.Parse(source, sourceFile: "test.fx");
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "");
        return result.Value.StrippedHlsl;
    }

    private static int LineCount(string text) => text.Split('\n').Length;

    [Theory]
    [InlineData("tex2Dlod", "sdTexture.SampleLevel(sdSampler, sdCoord.xy, sdCoord.w)")]
    [InlineData("tex2Dbias", "sdTexture.SampleBias(sdSampler, sdCoord.xy, sdCoord.w)")]
    [InlineData("tex2Dproj", "sdTexture.Sample(sdSampler, sdCoord.xy / sdCoord.w)")]
    public void SamplerStateBoundTexture_CallBecomesHelperCall_HelperBeforeTheFunction(string intrinsic, string body)
    {
        string source =
            "texture SpriteTexture;\n" +
            "sampler TexSampler = sampler_state { Texture = <SpriteTexture>; };\n" +
            "float4 PSMain(float4 t : TEXCOORD0) : COLOR0\n" +
            "{\n" +
            $"    return {intrinsic}(TexSampler, t);\n" +
            "}\n" + Technique;

        string stripped = Rewrite(source);

        stripped.ShouldContain($"return {intrinsic}_SD0(SpriteTexture, TexSampler, t);", Case.Sensitive);
        stripped.ShouldContain(
            $"float4 {intrinsic}_SD0(Texture2D sdTexture, SamplerState sdSampler, float4 sdCoord) {{ return {body}; }} float4 PSMain(",
            Case.Sensitive);
        stripped.ShouldNotContain($"{intrinsic}(", Case.Sensitive);
        stripped.ShouldContain("SamplerState TexSampler;", Case.Sensitive);
        LineCount(stripped).ShouldBe(LineCount(source), "no line may move: diagnostics keep their line numbers");
    }

    [Fact]
    public void BareSampler_UsesTheSynthesizedTexture()
    {
        string stripped = Rewrite(
            "sampler2D S;\n" +
            "float4 PSMain(float2 uv : TEXCOORD0) : COLOR0 { return tex2Dlod(S, float4(uv, 0, 2)); }" + Technique);

        stripped.ShouldContain("Texture2D S_SDTexture; SamplerState S;", Case.Sensitive);
        stripped.ShouldContain("tex2Dlod_SD0(S_SDTexture, S, float4(uv, 0, 2))", Case.Sensitive);
    }

    [Fact]
    public void Arguments_AreCopiedVerbatim_NestedCallsSwizzlesMacrosComments()
    {
        string stripped = Rewrite(
            "#define LOD(x) (x * 2)\n" +
            "sampler2D S;\n" +
            "float2 Warp(float2 v) { return v * 0.5; }\n" +
            "float4 PSMain(float2 uv : TEXCOORD0) : COLOR0\n" +
            "{ return tex2Dlod(S, /* coord */ float4(Warp(uv.yx).xy, 0, LOD(saturate(uv.x))).zyxw); }" + Technique);

        stripped.ShouldContain(
            "tex2Dlod_SD0(S_SDTexture, S, /* coord */ float4(Warp(uv.yx).xy, 0, LOD(saturate(uv.x))).zyxw)",
            Case.Sensitive);
    }

    [Fact]
    public void LegacyCallsNestedInTheCoordinate_AreRewrittenInPlace()
    {
        string stripped = Rewrite(
            "sampler2D A;\n" +
            "sampler2D B;\n" +
            "float4 PSMain(float2 uv : TEXCOORD0) : COLOR0\n" +
            "{ return tex2Dlod(A, tex2Dlod(B, float4(uv, 0, 1)) + tex2D(B, uv)); }" + Technique);

        stripped.ShouldContain(
            "tex2Dlod_SD0(A_SDTexture, A, tex2Dlod_SD0(B_SDTexture, B, float4(uv, 0, 1)) + B_SDTexture.Sample(B, uv))",
            Case.Sensitive);
        // One helper serves both calls: they sit in the same declaration and the same texture type.
        stripped.Split("float4 tex2Dlod_SD0(").Length.ShouldBe(2, "the helper is defined exactly once");
    }

    [Fact]
    public void MultiLineCall_KeepsEveryLineAndTheCoordinateOnItsOwnLine()
    {
        const string source =
            "sampler2D S;\n" +                                   // 1
            "float4 PSMain(float2 uv : TEXCOORD0) : COLOR0\n" +  // 2
            "{\n" +                                              // 3
            "    return tex2Dlod  // the call\n" +               // 4
            "    (\n" +                                          // 5
            "        S,\n" +                                     // 6
            "        float4(uv,\n" +                             // 7
            "               0, 3));\n" +                         // 8
            "}\n" + Technique;

        string stripped = Rewrite(source);
        string[] lines = stripped.Split('\n');

        LineCount(stripped).ShouldBe(LineCount(source));
        lines[1].ShouldStartWith("float4 tex2Dlod_SD0(Texture2D sdTexture", Case.Sensitive);
        lines[1].ShouldEndWith("float4 PSMain(float2 uv : TEXCOORD0) : SV_Target0", Case.Sensitive);
        lines[3].ShouldBe("    return tex2Dlod_SD0(S_SDTexture, ");
        lines[4].ShouldBe("");
        lines[5].ShouldBe("        S,");
        lines[6].ShouldBe("        float4(uv,");
        lines[7].ShouldBe("               0, 3));");
    }

    [Fact]
    public void EachDeclaration_GetsItsOwnHelper_SoBranchesNeverRedefineOne()
    {
        string stripped = Rewrite(
            "sampler2D S;\n" +
            "#if A\n" +
            "float4 Get(float2 uv) { return tex2Dlod(S, float4(uv, 0, 1)); }\n" +
            "#else\n" +
            "float4 Get(float2 uv) { return tex2Dlod(S, float4(uv, 0, 2)); }\n" +
            "#endif\n" +
            "float4 PSMain(float2 uv : TEXCOORD0) : COLOR0 { return Get(uv) + tex2Dbias(S, float4(uv, 0, 1)) + tex2Dlod(S, float4(uv, 0, 0)); }" +
            Technique);

        string[] lines = stripped.Split('\n');
        lines[2].ShouldStartWith("float4 tex2Dlod_SD0(", Case.Sensitive);
        lines[2].ShouldContain("float4 Get(float2 uv) { return tex2Dlod_SD0(S_SDTexture, S,", Case.Sensitive);
        lines[4].ShouldStartWith("float4 tex2Dlod_SD1(", Case.Sensitive);
        lines[4].ShouldContain("return tex2Dlod_SD1(S_SDTexture, S,", Case.Sensitive);
        lines[6].ShouldStartWith("float4 tex2Dbias_SD2(", Case.Sensitive);
        lines[6].ShouldContain(" float4 tex2Dlod_SD3(", Case.Sensitive);
        lines[6].ShouldContain("tex2Dbias_SD2(S_SDTexture, S, float4(uv, 0, 1)) + tex2Dlod_SD3(S_SDTexture, S, float4(uv, 0, 0))", Case.Sensitive);
    }

    [Fact]
    public void ConditionalInsideTheFunctionBody_IsFine_TheHelperNamesNoGlobal()
    {
        // The texture and sampler are declared only under USE_TEX, and the call sits in an
        // #if inside the body: the helper (placed before the function, outside that #if) must
        // not reference them, or the #else configuration would stop compiling.
        string stripped = Rewrite(
            "#if USE_TEX\n" +
            "sampler2D S;\n" +
            "#endif\n" +
            "float4 PSMain(float2 uv : TEXCOORD0) : COLOR0\n" +
            "{\n" +
            "#if USE_TEX\n" +
            "    return tex2Dlod(S, float4(uv, 0, 1));\n" +
            "#else\n" +
            "    return float4(uv, 0, 1);\n" +
            "#endif\n" +
            "}" + Technique);

        string helper = stripped.Split('\n')[3];
        helper.ShouldStartWith("float4 tex2Dlod_SD0(Texture2D sdTexture, SamplerState sdSampler, float4 sdCoord)", Case.Sensitive);
        helper.ShouldNotContain("S_SDTexture", Case.Sensitive);
        stripped.ShouldContain("return tex2Dlod_SD0(S_SDTexture, S, float4(uv, 0, 1));", Case.Sensitive);
    }

    [Fact]
    public void HelperFollowsATechniqueBlockAndAStruct()
    {
        string stripped = Rewrite(
            "sampler2D S;\n" +
            "technique First { pass P { PixelShader = compile ps_4_0 PSMain(); } }\n" +
            "struct VOut { float4 Pos : SV_Position; float2 UV : TEXCOORD0; };\n" +
            "float4 PSMain(VOut i) : COLOR0 { return tex2Dlod(S, float4(i.UV, 0, 1)); }\n");

        string[] lines = stripped.Split('\n');
        lines[1].Trim().ShouldBe("", "the technique block is erased");
        lines[2].ShouldStartWith("struct VOut", Case.Sensitive);
        lines[3].ShouldStartWith("float4 tex2Dlod_SD0(", Case.Sensitive);
        lines[3].ShouldContain("} float4 PSMain(VOut i)", Case.Sensitive);
    }

    [Fact]
    public void TypedTexture_TheHelperTakesItsDeclaredType()
    {
        // DXC and vkd3d refuse to pass a Texture2D<float> to a Texture2D parameter.
        string stripped = Rewrite(
            "Texture2D<float> Height;\n" +
            "sampler HeightSampler = sampler_state { Texture = <Height>; };\n" +
            "float4 PSMain(float2 uv : TEXCOORD0) : COLOR0 { return tex2Dlod(HeightSampler, float4(uv, 0, 0)); }" + Technique);

        stripped.ShouldContain("float4 tex2Dlod_SD0(Texture2D<float> sdTexture, SamplerState sdSampler, float4 sdCoord)", Case.Sensitive);
        stripped.ShouldContain("tex2Dlod_SD0(Height, HeightSampler, float4(uv, 0, 0))", Case.Sensitive);
    }

    [Fact]
    public void HelperName_SkipsANameTheSourceAlreadyUses()
    {
        string stripped = Rewrite(
            "sampler2D S;\n" +
            "static const float tex2Dlod_SD0 = 1;\n" +
            "float4 PSMain(float2 uv : TEXCOORD0) : COLOR0 { return tex2Dlod(S, float4(uv, 0, tex2Dlod_SD0)); }" + Technique);

        stripped.ShouldContain("float4 tex2Dlod_SD1(", Case.Sensitive);
        stripped.ShouldContain("return tex2Dlod_SD1(S_SDTexture, S, float4(uv, 0, tex2Dlod_SD0));", Case.Sensitive);
    }

    [Fact]
    public void UnboundSampler_FunctionParameter_IsLeftVerbatimLikeTex2D()
    {
        // A sampler function parameter is not a global the rewrite binds: the call is left for
        // the legacy-sampler recovery (and DirectX 11's vkd3d, which compiles it natively), the
        // same as tex2D on the same shape.
        string stripped = Rewrite(
            "float4 F(sampler2D s, float4 c) { return tex2Dlod(s, c); }\n" +
            "float4 PSMain(float2 uv : TEXCOORD0) : COLOR0 { return 0; }" + Technique);

        stripped.ShouldContain("return tex2Dlod(s, c);", Case.Sensitive);
        stripped.ShouldNotContain("_SD0", Case.Sensitive);
    }

    [Theory]
    [InlineData("tex2Dlod(S)", 1)]
    [InlineData("tex2Dlod(S, float4(uv, 0, 1), 2)", 3)]
    public void WrongArgumentCount_FailsWithFX0012(string call, int count)
    {
        var result = FxPreParser.Parse(
            "sampler2D S;\nfloat4 PSMain(float2 uv : TEXCOORD0) : COLOR0 { return " + call + "; }" + Technique,
            sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FxParseErrorCode.UnsupportedLegacyIntrinsic);
        result.Error.Message.ShouldContain("'tex2Dlod' takes two arguments", Case.Sensitive);
        result.Error.Message.ShouldContain($"this call has {count} arguments", Case.Sensitive);
        result.Error.Line.ShouldBe(2);
        result.Error.Column.ShouldBe(56);
    }

    [Fact]
    public void ConditionalSplittingTheFunctionHeader_FailsWithFX0012_AtTheCall()
    {
        // One body, two headers: no single position before "the declaration" is in both
        // branches, so the shape is refused instead of compiled in one configuration only.
        var result = FxPreParser.Parse(
            "sampler2D S;\n" +
            "#if A\n" +
            "float4 PSMain(float2 uv : TEXCOORD0) : COLOR0\n" +
            "#else\n" +
            "float4 PSMain(float2 uv : TEXCOORD1) : COLOR0\n" +
            "#endif\n" +
            "{\n" +
            "    return tex2Dlod(S, float4(uv, 0, 1));\n" +
            "}" + Technique,
            sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FxParseErrorCode.UnsupportedLegacyIntrinsic);
        result.Error.Message.ShouldContain("'#else', line 4", Case.Sensitive);
        result.Error.Line.ShouldBe(8);
    }

    [Theory]
    [InlineData("tex1D")]
    [InlineData("tex1Dlod")]
    [InlineData("tex3D")]
    [InlineData("tex3Dlod")]
    [InlineData("tex3Dbias")]
    [InlineData("texCUBE")]
    [InlineData("texCUBElod")]
    [InlineData("texCUBEproj")]
    public void OtherDimensions_StillFailWithFX0012_NamingTheIntrinsic(string intrinsic)
    {
        var result = FxPreParser.Parse(
            "sampler TexSampler = sampler_state { Texture = <SpriteTexture>; };\n" +
            $"float4 PSMain(float4 t : TEXCOORD0) : COLOR0 {{ return {intrinsic}(TexSampler, t); }}" + Technique,
            sourceFile: "test.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FxParseErrorCode.UnsupportedLegacyIntrinsic);
        result.Error.Message.ShouldContain($"'{intrinsic}'", Case.Sensitive);
        result.Error.Message.ShouldContain("not the 1D, 3D and cube families", Case.Sensitive);
    }

    [Theory]
    [InlineData("tex2Dlod")]
    [InlineData("tex2Dbias")]
    [InlineData("tex2Dproj")]
    public void PreserveSm3_LeavesTheCallVerbatim(string intrinsic)
    {
        string source =
            "sampler2D S;\n" +
            $"float4 PSMain(float4 t : TEXCOORD0) : COLOR0 {{ return {intrinsic}(S, t); }}" + Technique;

        var result = FxPreParser.Parse(source, "test.fx", FxSourceMode.PreserveSm3);

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain($"return {intrinsic}(S, t);", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("sampler2D S;", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldNotContain("_SD", Case.Sensitive);
    }

    [Fact]
    public void ACallFreeEffect_IsUntouched()
    {
        // No restructured intrinsic: no helper, no insertion, the same text the parser always
        // produced for a modern effect.
        const string source =
            "Texture2D T; SamplerState S;\n" +
            "float4 PSMain(float2 uv : TEXCOORD0) : SV_Target0 { return T.SampleLevel(S, uv, 1); }" + Technique;

        string stripped = Rewrite(source);
        stripped.ShouldStartWith("Texture2D T; SamplerState S;\nfloat4 PSMain(", Case.Sensitive);
        stripped.ShouldNotContain("_SD", Case.Sensitive);
    }
}
