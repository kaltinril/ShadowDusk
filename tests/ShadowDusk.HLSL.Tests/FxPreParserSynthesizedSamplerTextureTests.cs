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
    public void SamplerStateNamingAnUndeclaredTexture_DeclaresItUnderTheReferencedName()
    {
        // mgfxc reads `Texture = <Tex>` off the state block and emits a `Tex` parameter even
        // though nothing declares it; the rewrite used to hand DXC `Tex.Sample(...)` with no
        // `Tex` in scope.
        // The comment naming it does not count as a declaration.
        var result = FxPreParser.Parse(
            "// Tex is never declared\nsampler2D A = sampler_state { Texture = <Tex>; };" + Body, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldContain("Texture2D Tex; SamplerState A;", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("Tex.Sample(A, uv)", Case.Sensitive);
        // Its name is the author's, so it is not a synthesized companion.
        result.Value.SynthesizedSamplerTextures.ShouldBeEmpty();
    }

    [Fact]
    public void TwoStateBlocksNamingOneUndeclaredTexture_DeclareItOnce()
    {
        const string source = """
            sampler2D A = sampler_state { Texture = <Tex>; };
            sampler2D B = sampler_state { Texture = (Tex); MinFilter = Point; };
            float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) * tex2D(B, uv); }
            technique Main { pass P0 { PixelShader = compile ps_3_0 PS(); } }
            """;

        var result = FxPreParser.Parse(source, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        string stripped = result.Value.StrippedHlsl;
        stripped.ShouldContain("Texture2D Tex; SamplerState A;", Case.Sensitive);
        stripped.ShouldContain("SamplerState B;", Case.Sensitive);
        stripped.Split("Texture2D Tex;").Length.ShouldBe(2, "the texture must be declared exactly once");
    }

    [Theory]
    // Declared AFTER the sampler: still a declaration, so nothing is added.
    [InlineData("sampler2D A = sampler_state { Texture = <Tex>; }; Texture2D Tex;")]
    // Mentioned anywhere else (here a macro), so the rewrite stays exactly as it was.
    [InlineData("#define Tex OtherTex\nTexture2D OtherTex; sampler2D A = sampler_state { Texture = <Tex>; };")]
    public void ReferencedTextureMentionedElsewhere_IsLeftAlone(string declarations)
    {
        var result = FxPreParser.Parse(declarations + Body, sourceFile: "test.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.StrippedHlsl.ShouldNotContain("Texture2D Tex; SamplerState A;", Case.Sensitive);
        result.Value.StrippedHlsl.ShouldContain("SamplerState A;", Case.Sensitive);
    }
}
