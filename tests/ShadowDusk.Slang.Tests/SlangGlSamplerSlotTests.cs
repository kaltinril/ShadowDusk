#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Integration.Tests;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #252: on OpenGL, the real-slangc route must put a texture on the same sampler slot
/// the <c>.fx</c> route gives the equivalent hand-written HLSL. slangc numbers every
/// resource itself, and the GL allocator reads a <c>SamplerState : register(sN)</c> as an
/// author reservation, so before the fix a single texture landed on <c>ps_s1</c> where
/// SpriteBatch (unit 0) never reached it. The rendered proof is
/// <c>validation/SlangTexturedGl</c>; this pins the slot table on every host.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangGlSamplerSlotTests
{
    private const string PixelShader = """
        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return SpriteTexture.Sample(SpriteSampler, uv);
        }
        """;

    private const string FxPixelShader = """
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return SpriteTexture.Sample(SpriteSampler, uv);
        }

        technique T
        {
            pass P0
            {
                PixelShader = compile PS_SHADERMODEL MainPS();
            }
        }
        """;

    private const string FxHeader = """
        #if SM4
            #define PS_SHADERMODEL ps_4_0_level_9_1
        #else
            #define PS_SHADERMODEL ps_3_0
        #endif

        """;

    private static IReadOnlyList<MgfxSamplerRecord> SlangSamplers(string declarations)
    {
        var result = new SlangCompiler().Compile(
            declarations + "\n" + PixelShader,
            new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Slot.slang" });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        return MgfxBlobReader.Parse(result.Value.Data).Samplers;
    }

    private static IReadOnlyList<MgfxSamplerRecord> FxSamplers(string declarations)
    {
        var result = new EffectCompiler().Compile(
            FxHeader + declarations + "\n" + FxPixelShader,
            new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Slot.fx" });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        return MgfxBlobReader.Parse(result.Value.Data).Samplers;
    }

    [Fact]
    public void UnannotatedTexture_LandsOnUnitZero_LikeTheFxRoute()
    {
        const string decls = "Texture2D SpriteTexture;\nSamplerState SpriteSampler;\n";

        var slang = SlangSamplers(decls);
        var fx = FxSamplers(decls);

        slang.Count.ShouldBe(1);
        slang[0].TextureSlot.ShouldBe((byte)0);
        slang[0].SamplerSlot.ShouldBe((byte)0);
        slang.Select(s => (s.TextureSlot, s.SamplerSlot)).ShouldBe(fx.Select(s => (s.TextureSlot, s.SamplerSlot)));
    }

    [Fact]
    public void AuthorWrittenSamplerRegister_IsHonoured_LikeTheFxRoute()
    {
        // The author's own 'register(s0)' on a modern SamplerState is a reservation under the
        // .fx route's (mgfxc-measured) rule, which moves the pair to ps_s1. Stripping must not
        // touch it, or the two routes would disagree on hand-written intent.
        const string decls = "Texture2D SpriteTexture;\nSamplerState SpriteSampler : register(s0);\n";

        var slang = SlangSamplers(decls);
        var fx = FxSamplers(decls);

        slang.Count.ShouldBe(1);
        slang[0].TextureSlot.ShouldBe((byte)1);
        slang.Select(s => (s.TextureSlot, s.SamplerSlot)).ShouldBe(fx.Select(s => (s.TextureSlot, s.SamplerSlot)));
    }

    [Fact]
    public void VkBindingOnly_IsNotAGlReservation_LikeTheFxRoute()
    {
        // slangc drops vk::binding from its HLSL and invents register(s0); the .fx route never
        // reads vk::binding as a GL reservation, so the texture stays on unit 0 on both.
        const string decls = "Texture2D SpriteTexture;\n[[vk::binding(3)]] SamplerState SpriteSampler;\n";

        var slang = SlangSamplers(decls);
        var fx = FxSamplers(decls);

        slang.Count.ShouldBe(1);
        slang[0].TextureSlot.ShouldBe((byte)0);
        slang.Select(s => (s.TextureSlot, s.SamplerSlot)).ShouldBe(fx.Select(s => (s.TextureSlot, s.SamplerSlot)));
    }
}
