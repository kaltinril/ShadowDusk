#nullable enable

using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>Pure unit tests for <see cref="SlangcRegisterStripper"/> (issue #252).</summary>
public sealed class SlangcRegisterStripperTests
{
    private const string SlangcEmission = """
        Texture2D<float4 > SpriteTexture : register(t0);
        SamplerState SpriteSampler : register(s0);
        TextureCube<float4 > Env : register(t1, space0);
        SamplerComparisonState Shadow : register(s1);
        Texture2D<float4 > Arr[2] : register(t2);
        cbuffer Params : register(b0)
        {
            float Levels;
        }
        """;

    [Fact]
    public void Strip_RemovesEverySlangcNumberedTextureAndSamplerRegister()
    {
        string stripped = SlangcRegisterStripper.Strip(SlangcEmission, new HashSet<string>());

        stripped.ShouldContain("Texture2D<float4 > SpriteTexture;", Case.Sensitive);
        stripped.ShouldContain("SamplerState SpriteSampler;", Case.Sensitive);
        stripped.ShouldContain("TextureCube<float4 > Env;", Case.Sensitive);
        stripped.ShouldContain("SamplerComparisonState Shadow;", Case.Sensitive);
        stripped.ShouldContain("Texture2D<float4 > Arr[2];", Case.Sensitive);
        stripped.ShouldNotContain("register(t", Case.Sensitive);
        stripped.ShouldNotContain("register(s", Case.Sensitive);
    }

    [Fact]
    public void Strip_LeavesConstantBufferRegistersAlone()
    {
        SlangcRegisterStripper.Strip(SlangcEmission, new HashSet<string>())
            .ShouldContain("cbuffer Params : register(b0)", Case.Sensitive);
    }

    [Fact]
    public void Strip_KeepsTheRegisterOfAnAuthorBoundName()
    {
        string stripped = SlangcRegisterStripper.Strip(SlangcEmission, new HashSet<string> { "SpriteSampler" });

        stripped.ShouldContain("SamplerState SpriteSampler : register(s0);", Case.Sensitive);
        stripped.ShouldContain("Texture2D<float4 > SpriteTexture;", Case.Sensitive);
    }

    [Fact]
    public void AuthorBoundNames_FindsRegisterAnnotations_IncludingArrays()
    {
        const string source = """
            Texture2D Mask : register(t1);
            SamplerState MaskSampler: register( s1 );
            Texture2D Layers[4] : register(t4);
            Texture2D Free;
            """;

        var names = SlangcRegisterStripper.AuthorBoundNames(source);

        names.ShouldBe(new[] { "Mask", "MaskSampler", "Layers" }, ignoreOrder: true);
    }

    [Fact]
    public void AuthorBoundNames_IgnoresCommentsAndVkBinding()
    {
        // A register in a comment is not one; vk::binding is not an HLSL register and slangc's
        // HLSL drops it (measured), so it reserves nothing on the .fx route either.
        const string source = """
            // SamplerState Old : register(s0);
            /* Texture2D Gone : register(t0); */
            [[vk::binding(3)]] SamplerState S;
            """;

        SlangcRegisterStripper.AuthorBoundNames(source).ShouldBeEmpty();
    }
}
