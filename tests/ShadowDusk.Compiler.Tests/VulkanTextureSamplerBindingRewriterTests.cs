#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler.Internal;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// <see cref="VulkanTextureSamplerBindingRewriter"/> forces a texture+sampler pair onto
/// matching explicit HLSL registers so DXC's <c>-fvk-t-shift</c>/<c>-fvk-s-shift</c> land
/// them at the SAME raw SPIR-V binding — the only pattern confirmed (2026-07-18, minimal
/// repro against real DesktopVK) to draw correctly. Auto-numbered (no explicit register)
/// separate Texture2D/SamplerState declarations otherwise land at DIFFERENT raw bindings
/// and crash the native draw path.
///
/// Real usage always wraps the rewrite target in <c>#if VULKAN ... #endif</c> (one .fx
/// serves every target), but the rewriter itself scans the WHOLE file (see
/// <c>Rewrite_IgnoresFxPreParserSynthesizedTextureName</c> for why) — a texture is often
/// declared unconditionally, shared across all targets, with only its sampler differing
/// per <c>#if</c> branch.
/// </summary>
public sealed class VulkanTextureSamplerBindingRewriterTests
{
    [Fact]
    public void Rewrite_PairedTextureAndSampler_GetMatchingRegisters()
    {
        const string hlsl = """
            #if VULKAN
            Texture2D SpriteTexture;
            SamplerState SpriteTextureSampler;

            float4 PS() : SV_Target
            {
                return SpriteTexture.Sample(SpriteTextureSampler, float2(0, 0));
            }
            #endif
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.Should().Contain("Texture2D SpriteTexture : register(t0);");
        result.Should().Contain("SamplerState SpriteTextureSampler : register(s0);");
    }

    [Fact]
    public void Rewrite_TwoDistinctPairs_GetDistinctSharedIndices()
    {
        const string hlsl = """
            #if VULKAN
            Texture2D s0Texture;
            SamplerState s0;
            Texture2D _dissolveTex;
            SamplerState _dissolveTexSampler;

            float4 PS() : SV_Target
            {
                float4 a = s0Texture.Sample(s0, float2(0, 0));
                float4 b = _dissolveTex.Sample(_dissolveTexSampler, float2(0, 0));
                return a + b;
            }
            #endif
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.Should().Contain("Texture2D s0Texture : register(t0);");
        result.Should().Contain("SamplerState s0 : register(s0);");
        result.Should().Contain("Texture2D _dissolveTex : register(t1);");
        result.Should().Contain("SamplerState _dissolveTexSampler : register(s1);");
    }

    [Fact]
    public void Rewrite_NoTextureOrSampler_IsANoOp()
    {
        const string hlsl = "float4 PS() : SV_Target { return float4(1, 0, 0, 1); }";

        VulkanTextureSamplerBindingRewriter.Rewrite(hlsl).Should().Be(hlsl);
    }

    [Fact]
    public void Rewrite_AlreadyExplicitlyRegistered_IsLeftUntouched()
    {
        const string hlsl = """
            #if VULKAN
            Texture2D SpriteTexture : register(t3);
            SamplerState SpriteTextureSampler : register(s3);

            float4 PS() : SV_Target
            {
                return SpriteTexture.Sample(SpriteTextureSampler, float2(0, 0));
            }
            #endif
            """;

        VulkanTextureSamplerBindingRewriter.Rewrite(hlsl).Should().Be(hlsl);
    }

    [Fact]
    public void Rewrite_SharedTextureDeclaration_PairsWithVulkanOnlySampler()
    {
        // The common real-fixture shape (Grayscale/Invert/TintShader/Pixelated/Fading):
        // Texture2D is declared ONCE, unconditionally, shared across every target; only
        // the sampler differs per #if branch (modern SamplerState for Vulkan, legacy
        // sampler2D elsewhere). A #if-VULKAN-scoped scan would never find the shared
        // texture declaration (it sits outside every #if VULKAN span) and fail to pair
        // it with the sampler — confirmed as a real regression (2026-07-18) that broke
        // the combined-descriptor requirement and crashed the native Vulkan draw path.
        const string hlsl = """
            Texture2D SpriteTexture;

            #if VULKAN
            SamplerState SpriteTextureSampler;
            #else
            sampler2D SpriteTextureSampler = sampler_state { Texture = <SpriteTexture>; };
            #endif

            #if VULKAN
            float4 MainPS() : SV_Target
            {
                return SpriteTexture.Sample(SpriteTextureSampler, float2(0, 0));
            }
            #else
            float4 MainPS() : COLOR
            {
                return tex2D(SpriteTextureSampler, float2(0, 0));
            }
            #endif
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.Should().Contain("Texture2D SpriteTexture : register(t0);");
        result.Should().Contain("SamplerState SpriteTextureSampler : register(s0);");
    }

    [Fact]
    public void Rewrite_IgnoresFxPreParserSynthesizedTextureName()
    {
        // Confirmed root cause (2026-07-18): FxPreParser's existing legacy-sampler2D-to-
        // modern-syntax conversion runs over the whole file BEFORE this rewrite, and for
        // a bare `sampler s0;` (no texture reference) it synthesizes a paired texture
        // named "<sampler>_SDTexture" — here, in the #else (non-Vulkan) branch. This
        // rewrite must never touch that synthesized name, or it corrupts a declaration
        // DXC discards anyway for a Vulkan compile, while still correctly pairing the
        // real #if-VULKAN-branch declarations ("s0Texture"/"s0").
        const string hlsl = """
            #if VULKAN
            Texture2D s0Texture;
            SamplerState s0;
            #else
            Texture2D s0_SDTexture;
            SamplerState s0;
            #endif

            #if VULKAN
            float4 PS() : SV_Target
            {
                return s0Texture.Sample(s0, float2(0, 0));
            }
            #else
            float4 PS() : COLOR
            {
                return s0_SDTexture.Sample(s0, float2(0, 0));
            }
            #endif
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.Should().Contain("Texture2D s0Texture : register(t0);");
        // "s0" appears in both branches; only the pairing with the real Vulkan-branch
        // texture ("s0Texture", index 0) determines its register.
        result.Should().Contain("SamplerState s0 : register(s0);");
        // The synthesized name is never registered — left exactly as FxPreParser wrote it.
        result.Should().Contain("Texture2D s0_SDTexture;");
        result.Should().NotContain("s0_SDTexture : register");
    }
}
