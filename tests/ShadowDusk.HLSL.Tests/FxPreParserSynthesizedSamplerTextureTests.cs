#nullable enable
using ShadowDusk.HLSL;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// The pre-parser's record of which textures the SM4 rewrite INVENTED for a texture-less
/// legacy sampler (<see cref="Ast.FxParseResult.SynthesizedSamplerTextures"/>), which the
/// compiler uses to give the effect parameter mgfxc's name (the sampler's), and the
/// declaration it adds for a <c>sampler_state</c> texture the source never declares.
/// </summary>
public sealed class FxPreParserSynthesizedSamplerTextureTests
{
    private const string Body = """

        float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv); }
        technique Main { pass P0 { PixelShader = compile ps_3_0 PS(); } }
        """;

    [Theory]
    [InlineData("sampler2D A;")]
    [InlineData("sampler A;")]
    [InlineData("sampler2D A : register(s1);")]
    [InlineData("sampler2D A = sampler_state { MinFilter = Point; };")]
    public void TextureLessLegacySampler_IsRecordedAsSynthesized(string declaration)
    {
        var result = FxPreParser.Parse(declaration + Body, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.SynthesizedSamplerTextures.ShouldBe(
            new Dictionary<string, string> { ["A"] = "A_SDTexture" });
        result.Value.StrippedHlsl.ShouldContain("Texture2D A_SDTexture; SamplerState A;", Case.Sensitive);
    }

    [Fact]
    public void SamplerStateNamingADeclaredTexture_IsNotSynthesized()
    {
        var result = FxPreParser.Parse(
            "texture T; sampler2D A = sampler_state { Texture = <T>; };" + Body, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.SynthesizedSamplerTextures.ShouldBeEmpty();
        result.Value.StrippedHlsl.ShouldContain("SamplerState A;", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldNotContain("_SDTexture", Case.Sensitive);
    }

    [Fact]
    public void SamplerStateNamingATextureTheMainFileNeverDeclares_IsACandidate_NotADeclaration()
    {
        // mgfxc reads `Texture = <Tex>` off the state block and emits a `Tex` parameter even
        // though nothing declares it. The pre-parser cannot see #include'd files or expand
        // macros, either of which can declare `Tex`, so it only RECORDS the candidate; the
        // compiler declares it after checking the preprocessed source. The comment naming it
        // does not count as a mention.
        var result = FxPreParser.Parse(
            "// Tex is never declared\nsampler2D A = sampler_state { Texture = <Tex>; };" + Body, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.UndeclaredStateTextures.ShouldBe(new Dictionary<string, string> { ["A"] = "Tex" });
        result.Value.StrippedHlsl.ShouldNotContain("Texture2D Tex", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("Tex.Sample(A, uv)", Case.Sensitive);
        result.Value.SynthesizedSamplerTextures.ShouldBeEmpty();
    }

    [Fact]
    public void DeclareUndeclaredStateTextures_DeclaresOncePerTexture_WhenThePreprocessedSourceHasNone()
    {
        const string source = """
            sampler2D A = sampler_state { Texture = <Tex>; };
            sampler2D B = sampler_state { Texture = (Tex); MinFilter = Point; };
            float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) * tex2D(B, uv); }
            technique Main { pass P0 { PixelShader = compile ps_3_0 PS(); } }
            """;

        var parsed = FxPreParser.Parse(source, sourceFile: "test.fx");
        parsed.IsSuccess.ShouldBeTrue();
        var declared = FxPreParser.DeclareUndeclaredStateTextures(parsed.Value.StrippedHlsl, "test.fx", parsed.Value);

        declared.IsSuccess.ShouldBeTrue();
        declared.Value.ShouldContain("Texture2D Tex; SamplerState A;", Case.Sensitive);
        declared.Value.Split("Texture2D Tex;").Length.ShouldBe(2, "the texture must be declared exactly once");
    }

    [Theory]
    // Declared by a macro the pre-parser cannot expand (the case the raw reading got wrong).
    [InlineData("#define DECL_TEX(x) Texture2D x##Tex;\nDECL_TEX(Mask)\nsampler2D A = sampler_state { Texture = <MaskTex>; };", "MaskTex")]
    // Declared after the sampler, in both spellings.
    [InlineData("sampler2D A = sampler_state { Texture = <Tex>; };\ntexture Tex;", "Tex")]
    [InlineData("sampler2D A = sampler_state { Texture = <Tex>; };\nTexture2D Tex : register(t2);", "Tex")]
    public void DeclareUndeclaredStateTextures_LeavesADeclaredTextureAlone(string declarations, string texture)
    {
        var parsed = FxPreParser.Parse(declarations + Body.Replace("tex2D(A, uv)", "tex2D(A, uv)"), sourceFile: "test.fx");
        parsed.IsSuccess.ShouldBeTrue();
        var declared = FxPreParser.DeclareUndeclaredStateTextures(parsed.Value.StrippedHlsl, "test.fx", parsed.Value);

        declared.IsSuccess.ShouldBeTrue();
        declared.Value.ShouldNotContain($"Texture2D {texture}; SamplerState A;", Case.Sensitive);
    }
}
