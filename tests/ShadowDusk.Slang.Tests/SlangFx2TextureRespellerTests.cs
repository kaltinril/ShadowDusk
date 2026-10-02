#nullable enable

using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>Pure unit tests for <see cref="SlangFx2TextureRespeller"/> (issue #230).</summary>
public sealed class SlangFx2TextureRespellerTests
{
    private const string SlangcEmission = """
        Texture2D<float4 > SpriteTexture;
        SamplerState SpriteSampler;

        float4 MainPS(float4 pos_0 : SV_Position, float2 uv_0 : TEXCOORD0) : SV_TARGET
        {
            float4 c_0 = SpriteTexture.Sample(SpriteSampler, uv_0);
            return float4(1.0f - c_0.xyz, c_0.w);
        }
        """;

    private static string Respell(string hlsl)
    {
        string? result = SlangFx2TextureRespeller.TryRespell(hlsl, out string? unsupported);
        result.ShouldNotBeNull(unsupported);
        unsupported.ShouldBeNull();
        return result;
    }

    private static string Rejected(string hlsl)
    {
        SlangFx2TextureRespeller.TryRespell(hlsl, out string? unsupported).ShouldBeNull();
        unsupported.ShouldNotBeNull();
        return unsupported;
    }

    [Fact]
    public void TextureObjects_BecomeDx9EffectSyntax()
    {
        string fx = Respell(SlangcEmission);

        fx.ShouldContain("texture2D SpriteTexture;", Case.Sensitive);
        fx.ShouldContain("sampler2D SpriteSampler = sampler_state { Texture = <SpriteTexture>; };", Case.Sensitive);
        fx.ShouldContain("float4 c_0 = tex2D(SpriteSampler, uv_0);", Case.Sensitive);
        fx.ShouldNotContain("Texture2D", Case.Sensitive);
        fx.ShouldNotContain("SamplerState", Case.Sensitive);
        fx.ShouldNotContain(".Sample", Case.Sensitive);
    }

    [Fact]
    public void EverythingElse_IsUntouched()
    {
        Respell(SlangcEmission).ShouldContain("return float4(1.0f - c_0.xyz, c_0.w);", Case.Sensitive);
    }

    [Fact]
    public void TextureFreeText_ComesBackUnchanged()
    {
        const string hlsl = "float4 MainPS(float2 uv : TEXCOORD0) : SV_TARGET { return float4(uv, 0, 1); }";
        Respell(hlsl).ShouldBe(hlsl);
    }

    [Fact]
    public void AuthorSamplerRegister_IsKept_TextureRegisterDropped()
    {
        string fx = Respell(SlangcEmission
            .Replace("SamplerState SpriteSampler;", "SamplerState SpriteSampler : register(s2);")
            .Replace("Texture2D<float4 > SpriteTexture;", "Texture2D<float4 > SpriteTexture : register(t2);"));

        fx.ShouldContain("sampler2D SpriteSampler : register(s2) = sampler_state { Texture = <SpriteTexture>; };", Case.Sensitive);
        fx.ShouldContain("texture2D SpriteTexture;", Case.Sensitive);
    }

    [Fact]
    public void NestedSample_InTheUvArgument_IsRespelled()
    {
        const string hlsl = """
            Texture2D<float4 > A;
            SamplerState SA;
            Texture2D<float4 > B;
            SamplerState SB;
            float4 F(float2 uv) { return A.Sample(SA, B.Sample(SB, uv).xy); }
            """;

        string fx = Respell(hlsl);

        fx.ShouldContain("return tex2D(SA, tex2D(SB, uv).xy);", Case.Sensitive);
        fx.ShouldContain("sampler2D SA = sampler_state { Texture = <A>; };", Case.Sensitive);
        fx.ShouldContain("sampler2D SB = sampler_state { Texture = <B>; };", Case.Sensitive);
    }

    [Fact]
    public void OneSamplerWithTwoTextures_IsRejected()
    {
        const string hlsl = """
            Texture2D<float4 > A;
            Texture2D<float4 > B;
            SamplerState S;
            float4 F(float2 uv) { return A.Sample(S, uv) + B.Sample(S, uv); }
            """;

        Rejected(hlsl).ShouldContain("two textures", Case.Sensitive);
    }

    [Theory]
    [InlineData("SpriteTexture.Sample(SpriteSampler, uv_0, int2(1, 0))", "3 arguments")]
    [InlineData("SpriteTexture.SampleLevel(SpriteSampler, uv_0, 0.0f)", "SampleLevel")]
    [InlineData("SpriteTexture.Load(int3(0, 0, 0))", "Load")]
    public void UnmodeledTextureCalls_AreRejectedByName(string call, string named)
    {
        Rejected(SlangcEmission.Replace("SpriteTexture.Sample(SpriteSampler, uv_0)", call))
            .ShouldContain(named, Case.Sensitive);
    }

    [Fact]
    public void NonTwoDimensionalTexture_IsRejectedByName()
    {
        const string hlsl = """
            TextureCube<float4 > Env;
            SamplerState S;
            float4 F(float3 d) { return Env.Sample(S, d); }
            """;

        Rejected(hlsl).ShouldContain("TextureCube", Case.Sensitive);
    }
}
