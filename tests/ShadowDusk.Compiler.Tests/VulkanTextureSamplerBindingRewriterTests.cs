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
    public void Rewrite_TwoTexturesThroughOneSamplerInOnePath_FailsLoudlyWithSD0028()
    {
        // Bug-hunt 2026-07-27 M5: this shape used to co-locate both textures onto ONE
        // combined descriptor, leaving the second texture unbound at draw — a silent
        // wrong render on DesktopVK (the rewriter's own KNOWN LIMITATION comment).
        const string hlsl = """
            Texture2D DiffuseTex;
            Texture2D LightmapTex;
            SamplerState SharedSampler;

            float4 PS() : SV_Target
            {
                return DiffuseTex.Sample(SharedSampler, float2(0, 0))
                     + LightmapTex.Sample(SharedSampler, float2(0, 0));
            }
            """;

        var act = () => VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        var error = act.Should().Throw<VulkanSamplerSharingException>().Which.Error;
        error.Code.Should().Be("SD0028");
        error.Line.Should().BeGreaterThan(0, "the diagnostic must point at the offending Sample call");
        error.Message.Should().Contain("DiffuseTex").And.Contain("LightmapTex").And.Contain("SharedSampler");
    }

    [Fact]
    public void Rewrite_SameSamplerAcrossMutuallyExclusiveBranches_IsStillLegal()
    {
        // The legal shape the detector must NOT flag: only one #if branch survives a
        // compile, so cross-branch re-pairing can never co-locate two live textures.
        const string hlsl = """
            Texture2D TexA;
            Texture2D TexB;
            SamplerState SharedSampler;

            float4 PS() : SV_Target
            {
            #if VARIANT_A
                return TexA.Sample(SharedSampler, float2(0, 0));
            #else
                return TexB.Sample(SharedSampler, float2(0, 0));
            #endif
            }
            """;

        var act = () => VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        act.Should().NotThrow();
    }

    [Fact]
    public void Rewrite_OneTextureSampledRepeatedlyThroughOneSampler_IsLegal()
    {
        const string hlsl = """
            Texture2D Tex;
            SamplerState Samp;

            float4 PS() : SV_Target
            {
                return Tex.Sample(Samp, float2(0, 0)) + Tex.Sample(Samp, float2(1, 1));
            }
            """;

        var act = () => VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        act.Should().NotThrow();
    }

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
    public void Rewrite_InheritedIndexNeverCollides_EvenWhenTheInheritingPairIsDeclaredFirst()
    {
        // The mirror image of the test below, and the order the first fix missed: the
        // collision guard only runs on the INHERITING path, so when that pair is processed
        // first it claims the index and the later explicitly-registered texture is dropped
        // on top of it. Explicit textures are now assigned before inheriting ones.
        const string hlsl = """
            Texture2D TexB;
            SamplerState SampB : register(s1);
            Texture2D TexA : register(t1);
            SamplerState SampA;

            float4 PS(float2 uv : TEXCOORD0) : SV_Target
            {
                return TexA.Sample(SampA, uv) * TexB.Sample(SampB, uv);
            }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        int texA = IndexOf(result, "TexA", 't');
        int texB = IndexOf(result, "TexB", 't');

        texA.Should().NotBe(texB, "two textures on one binding is an invalid descriptor set");
        IndexOf(result, "SampA", 's').Should().Be(texA, "a pair must stay co-located");
        IndexOf(result, "SampB", 's').Should().Be(texB, "a pair must stay co-located");
        texA.Should().Be(1, "an explicit texture register stays authoritative among textures");
    }

    [Fact]
    public void Rewrite_InheritedIndexNeverCollidesWithAnotherPairsExplicitRegister()
    {
        // The "explicit registers are reserved" guarantee had a hole: `reserved` was only
        // consulted by the auto-assign path, so a pair that INHERITED its index from an
        // explicitly-registered half could land on an index another texture already held.
        // Here TexA is fixed at t1, and TexB inherits 1 from SampB's explicit s1 — both
        // textures ended up on binding 33, the invalid descriptor layout that
        // access-violates in MonoGame's descriptor writer (the issue #145 crash shape).
        const string hlsl = """
            Texture2D TexA : register(t1);
            SamplerState SampA;
            Texture2D TexB;
            SamplerState SampB : register(s1);

            float4 PS(float2 uv : TEXCOORD0) : SV_Target
            {
                return TexA.Sample(SampA, uv) * TexB.Sample(SampB, uv);
            }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        int texA = IndexOf(result, "TexA", 't');
        int texB = IndexOf(result, "TexB", 't');
        int sampA = IndexOf(result, "SampA", 's');
        int sampB = IndexOf(result, "SampB", 's');

        texA.Should().NotBe(texB, "two textures on one binding is an invalid descriptor set");
        sampA.Should().Be(texA, "a pair must stay co-located");
        sampB.Should().Be(texB, "a pair must stay co-located, even if that overrides its own explicit register");
    }

    [Fact]
    public void Rewrite_GatherCall_PairsTheTextureWithItsSampler()
    {
        // Gather/GatherRed/GatherCmp take a sampler exactly as Sample does. The pairing scan
        // matched only Sample*, so a Gather-only texture/sampler pair was left un-co-located
        // at two separate bindings — the same split-descriptor shape as the legacy-sampler bug.
        const string hlsl = """
            Texture2D ShadowMap;
            SamplerState ShadowSampler;

            float4 PS(float2 uv : TEXCOORD0) : SV_Target
            {
                return ShadowMap.Gather(ShadowSampler, uv);
            }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        IndexOf(result, "ShadowMap", 't').Should().Be(IndexOf(result, "ShadowSampler", 's'));
    }

    [Theory]
    [InlineData("Texture2DArray", "SamplerState")]
    [InlineData("Texture2D", "SamplerComparisonState")]
    public void Rewrite_ArrayAndComparisonDeclarations_AreSeenAndPaired(string textureType, string samplerType)
    {
        // Both shapes exist in MonoGame's own vendored corpus (TextureArrayEffect.fx,
        // CustomSpriteBatchEffectComparisonSampler.fx). Neither matched the declaration
        // regexes, so they were invisible: never paired, and their explicit registers never
        // added to the reserved set (so an auto-assigned pair could be handed the same index).
        string hlsl = $$"""
            {{textureType}} Tex;
            {{samplerType}} Samp;

            float4 PS(float2 uv : TEXCOORD0) : SV_Target
            {
                return Tex.Sample(Samp, float3(uv, 0));
            }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.Should().Contain($"{textureType} Tex : register(t0);");
        result.Should().Contain($"{samplerType} Samp : register(s0);");
    }

    /// <summary>Reads back the register index a declaration was assigned.</summary>
    private static int IndexOf(string hlsl, string name, char kind)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            hlsl, $@"\b{name}\s*:\s*register\s*\(\s*{kind}(\d+)\s*\)");
        m.Success.Should().BeTrue($"{name} must carry a {kind}-register; source was:\n{hlsl}");
        return int.Parse(m.Groups[1].Value);
    }

    [Theory]
    [InlineData("Texture1D")]
    [InlineData("Texture3D")]
    [InlineData("TextureCube")]
    [InlineData("Texture2D<float4>")]
    public void Rewrite_EveryTextureDimensionality_IsPaired(string textureType)
    {
        // Issue #145 widened the declaration match beyond plain Texture2D: an unpaired
        // TextureCube/Texture3D auto-numbered to raw binding 0/1 while its sampler moved to
        // 32/33, and MonoGame recovers the texture slot as (binding - 32), indexing its
        // texture array at -32 (a native access violation at draw). Every dimensionality and
        // the templated form must therefore be paired. All existing unit tests use plain
        // Texture2D, so narrowing the regex back left them green.
        string hlsl = $$"""
            #if VULKAN
            {{textureType}} SpriteTexture;
            SamplerState SpriteTextureSampler;

            float4 PS() : SV_Target
            {
                return SpriteTexture.Sample(SpriteTextureSampler, float3(0, 0, 0));
            }
            #endif
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.Should().Contain($"{textureType} SpriteTexture : register(t0);");
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
    public void Rewrite_PairsFxPreParserSynthesizedTextureWithItsSampler()
    {
        // FxPreParser's legacy-sampler2D-to-modern-syntax conversion runs over the whole file
        // BEFORE this rewrite, and for a bare `sampler s0;` (no texture reference) it
        // synthesizes a paired texture named "<sampler>_SDTexture".
        //
        // These synthesized names were once EXCLUDED from the rewrite outright, which was the
        // root cause of issue #145's native access violation: the exclusion left the pair
        // un-co-located (image auto-numbered at raw binding 0, sampler shifted to 32), and
        // MonoGame's native descriptor writer turns a binding-0 image into
        // `device->textures[stage][0 - 32]`. They are now paired like any other declaration —
        // BOTH branches end up on the same index, and only one survives the compile.
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
        result.Should().Contain("SamplerState s0 : register(s0);");
        // The synthesized texture shares its sampler's index, so whichever branch survives
        // yields ONE combined image-sampler descriptor.
        result.Should().Contain("Texture2D s0_SDTexture : register(t0);");
    }

    [Fact]
    public void Rewrite_LegacyOnlySource_PairsEverySynthesizedTexture()
    {
        // The issue-#145 legacy shape: no #if VULKAN branch at all, so every texture in the
        // file is one FxPreParser synthesized. Each must land on its sampler's index.
        const string hlsl = """
            Texture2D TextureSampler_SDTexture; SamplerState TextureSampler;
            Texture2D FontSampler_SDTexture; SamplerState FontSampler;

            float4 PS() : SV_Target
            {
                return TextureSampler_SDTexture.Sample(TextureSampler, float2(0, 0))
                     + FontSampler_SDTexture.Sample(FontSampler, float2(0, 0));
            }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.Should().Contain("Texture2D TextureSampler_SDTexture : register(t0);");
        result.Should().Contain("SamplerState TextureSampler : register(s0);");
        result.Should().Contain("Texture2D FontSampler_SDTexture : register(t1);");
        result.Should().Contain("SamplerState FontSampler : register(s1);");
    }

    [Fact]
    public void Rewrite_AutoAssignedPair_NeverCollidesWithAnExplicitRegister()
    {
        // -fvk-t-shift and -fvk-s-shift both add 32, so t2 and s2 occupy the SAME raw binding:
        // an auto-assigned pair that reused index 2 would collide with the explicitly
        // registered pair and produce two descriptor-set-layout bindings at one binding
        // number, which is invalid. Explicit indices are reserved. (Upstream apos-shapes.fx
        // is exactly this shape: register(s0), an unregistered sampler, and register(s2).)
        const string hlsl = """
            Texture2D ATex : register(t0); SamplerState ASamp : register(s0);
            Texture2D BTex; SamplerState BSamp;
            Texture2D CTex : register(t2); SamplerState CSamp : register(s2);

            float4 PS() : SV_Target
            {
                return ATex.Sample(ASamp, float2(0, 0))
                     + BTex.Sample(BSamp, float2(0, 0))
                     + CTex.Sample(CSamp, float2(0, 0));
            }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        // Explicit declarations are byte-identical…
        result.Should().Contain("Texture2D ATex : register(t0);");
        result.Should().Contain("Texture2D CTex : register(t2);");
        // …and the auto-assigned pair skips both reserved indices (0 and 2) onto 1.
        result.Should().Contain("Texture2D BTex : register(t1);");
        result.Should().Contain("SamplerState BSamp : register(s1);");
    }

    [Fact]
    public void Rewrite_UnregisteredTexturePairedWithRegisteredSampler_MirrorsTheSamplerIndex()
    {
        const string hlsl = """
            Texture2D Tex;
            SamplerState Samp : register(s3);

            float4 PS() : SV_Target { return Tex.Sample(Samp, float2(0, 0)); }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.Should().Contain("Texture2D Tex : register(t3);");
        result.Should().Contain("SamplerState Samp : register(s3);");
    }
}
