#nullable enable

using Shouldly;
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

        var error = Should.Throw<VulkanSamplerSharingException>(act).Error;
        error.Code.ShouldBe("SD0028");
        error.Line.ShouldBeGreaterThan(0, customMessage: "the diagnostic must point at the offending Sample call");
        error.Message.ShouldContain("DiffuseTex", Case.Sensitive);
        error.Message.ShouldContain("LightmapTex", Case.Sensitive);
        error.Message.ShouldContain("SharedSampler", Case.Sensitive);
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

        Should.NotThrow(act);
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

        Should.NotThrow(act);
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

        result.ShouldContain("Texture2D SpriteTexture : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState SpriteTextureSampler : register(s0);", Case.Sensitive);
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

        texA.ShouldNotBe(texB, customMessage: "two textures on one binding is an invalid descriptor set");
        IndexOf(result, "SampA", 's').ShouldBe(texA, customMessage: "a pair must stay co-located");
        IndexOf(result, "SampB", 's').ShouldBe(texB, customMessage: "a pair must stay co-located");
        texA.ShouldBe(1, customMessage: "an explicit texture register stays authoritative among textures");
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

        texA.ShouldNotBe(texB, customMessage: "two textures on one binding is an invalid descriptor set");
        sampA.ShouldBe(texA, customMessage: "a pair must stay co-located");
        sampB.ShouldBe(texB, customMessage: "a pair must stay co-located, even if that overrides its own explicit register");
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

        IndexOf(result, "ShadowMap", 't').ShouldBe(IndexOf(result, "ShadowSampler", 's'));
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

        result.ShouldContain($"{textureType} Tex : register(t0);", Case.Sensitive);
        result.ShouldContain($"{samplerType} Samp : register(s0);", Case.Sensitive);
    }

    /// <summary>Reads back the register index a declaration was assigned.</summary>
    private static int IndexOf(string hlsl, string name, char kind)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            hlsl, $@"\b{name}\s*:\s*register\s*\(\s*{kind}(\d+)\s*\)");
        m.Success.ShouldBeTrue($"{name} must carry a {kind}-register; source was:\n{hlsl}");
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

        result.ShouldContain($"{textureType} SpriteTexture : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState SpriteTextureSampler : register(s0);", Case.Sensitive);
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

        result.ShouldContain("Texture2D s0Texture : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState s0 : register(s0);", Case.Sensitive);
        result.ShouldContain("Texture2D _dissolveTex : register(t1);", Case.Sensitive);
        result.ShouldContain("SamplerState _dissolveTexSampler : register(s1);", Case.Sensitive);
    }

    [Fact]
    public void Rewrite_NoTextureOrSampler_IsANoOp()
    {
        const string hlsl = "float4 PS() : SV_Target { return float4(1, 0, 0, 1); }";

        VulkanTextureSamplerBindingRewriter.Rewrite(hlsl).ShouldBe(hlsl);
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

        VulkanTextureSamplerBindingRewriter.Rewrite(hlsl).ShouldBe(hlsl);
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

        result.ShouldContain("Texture2D SpriteTexture : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState SpriteTextureSampler : register(s0);", Case.Sensitive);
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

        result.ShouldContain("Texture2D s0Texture : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState s0 : register(s0);", Case.Sensitive);
        // The synthesized texture shares its sampler's index, so whichever branch survives
        // yields ONE combined image-sampler descriptor.
        result.ShouldContain("Texture2D s0_SDTexture : register(t0);", Case.Sensitive);
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

        result.ShouldContain("Texture2D TextureSampler_SDTexture : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState TextureSampler : register(s0);", Case.Sensitive);
        result.ShouldContain("Texture2D FontSampler_SDTexture : register(t1);", Case.Sensitive);
        result.ShouldContain("SamplerState FontSampler : register(s1);", Case.Sensitive);
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
        result.ShouldContain("Texture2D ATex : register(t0);", Case.Sensitive);
        result.ShouldContain("Texture2D CTex : register(t2);", Case.Sensitive);
        // …and the auto-assigned pair skips both reserved indices (0 and 2) onto 1.
        result.ShouldContain("Texture2D BTex : register(t1);", Case.Sensitive);
        result.ShouldContain("SamplerState BSamp : register(s1);", Case.Sensitive);
    }

    [Fact]
    public void Rewrite_UnregisteredTexturePairedWithRegisteredSampler_TakesTheTextureRegisterDirectXGivesIt()
    {
        // The pair's index is the effect's Vulkan slot (binding - 32). DirectX 11 and 12 put an
        // unregistered texture at t0 whatever its sampler's register, and SpriteBatch binds its
        // texture at slot 0, so the pair is t0/s0 here too. This used to MIRROR the sampler's s3
        // onto the texture (t3): invisible while the record wrote an ordinal slot, but the
        // descriptor then read slot 3, where neither DirectX nor SpriteBatch puts the texture.
        const string hlsl = """
            Texture2D Tex;
            SamplerState Samp : register(s3);

            float4 PS() : SV_Target { return Tex.Sample(Samp, float2(0, 0)); }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.ShouldContain("Texture2D Tex : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState Samp : register(s0);", Case.Sensitive);
    }

    [Fact]
    public void Rewrite_AReferencedUnpairedSamplersExplicitRegister_StaysReserved()
    {
        // A sampler no texture is sampled through, but that the shader still uses (here passed
        // to a helper), keeps its own binding, so an auto-assigned texture must not land on it
        // (two descriptors at one binding).
        const string hlsl = """
            SamplerState Loose : register(s0);
            Texture2D Tex;
            SamplerState Samp;

            float4 Helper(SamplerState s) { return 0; }
            float4 PS() : SV_Target { return Tex.Sample(Samp, float2(0, 0)) + Helper(Loose); }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.ShouldContain("SamplerState Loose : register(s0);", Case.Sensitive);
        result.ShouldContain("Texture2D Tex : register(t1);", Case.Sensitive);
        result.ShouldContain("SamplerState Samp : register(s1);", Case.Sensitive);
    }

    [Fact]
    public void Rewrite_ADeadSamplersExplicitRegister_DoesNotMoveATextureOffItsDirectXRegister()
    {
        // `U` is named only in its declaration: dead code DXC removes. DirectX 11 and 12 put the
        // unregistered `A` at t0, and so must this (it used to be pushed to t1 by U's s0).
        const string hlsl = """
            Texture2D A;
            SamplerState S : register(s1);
            SamplerState U : register(s0);

            float4 PS(float2 uv : TEXCOORD0) : SV_Target { return A.Sample(S, uv); }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.ShouldContain("Texture2D A : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState S : register(s0);", Case.Sensitive);
        // The dead declaration moves off the register its texture now holds.
        result.ShouldContain("SamplerState U : register(s1);", Case.Sensitive);
    }

    [Theory]
    [InlineData("Texture2D A : register(t1, space0);", "SamplerState S : register(s1, space0);")]
    [InlineData("Texture2D A : register(t1,space1);", "SamplerState S : register( s1 , space3 );")]
    public void Rewrite_DropsAnExplicitRegisterSpace(string texture, string sampler)
    {
        // A register space is a Vulkan descriptor set; MonoGame's DesktopVK has one per stage, so
        // an explicit space can only park the resource in a set the runtime never binds (the
        // pixel shader's resources must be in set 1). mgfxc 3.8.5 keeps it and its build draws
        // (0,0,0,0) for `register(t1, space0)` in a pixel shader.
        string hlsl = $$"""
            {{texture}}
            {{sampler}}
            cbuffer P : register(b0, space0) { float4 Tint; };

            float4 PS(float2 uv : TEXCOORD0) : SV_Target { return A.Sample(S, uv) * Tint; }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.ShouldContain("Texture2D A : register(t1);", Case.Sensitive);
        result.ShouldContain("SamplerState S : register(s1);", Case.Sensitive);
        result.ShouldContain("cbuffer P : register(b0) {", Case.Sensitive);
        result.ShouldNotContain("space", Case.Sensitive);
    }

    [Fact]
    public void StripRegisterSpaces_WithNoSpace_ReturnsTheSameText()
    {
        const string hlsl = "Texture2D A : register(t1); // register(t2, space0) in a comment stays\n";

        VulkanTextureSamplerBindingRewriter.StripRegisterSpaces(hlsl).ShouldBeSameAs(hlsl);
    }

    [Fact]
    public void Rewrite_DeclarationInsideAComment_IsNotAResource()
    {
        // examples/ExLegacyTextureDiscard.fx's header says "rewrites `texture T;` ->
        // `Texture2D T;`". That comment used to be collected as a texture T, which took index 0
        // and pushed the shader's real pair to t1, so on Vulkan its sprite sampler read slot 1
        // instead of the slot 0 SpriteBatch binds (DirectX 11/12 and OpenGL put it at 0).
        const string hlsl = """
            // ShadowDusk rewrites `texture T;` -> `Texture2D T;` so the resource exists.
            /* Texture2D Block; SamplerState BlockSampler; Block.Sample(BlockSampler, uv) */
            Texture2D SpriteTex;
            SamplerState SpriteSampler;

            float4 PS(float2 uv : TEXCOORD0) : SV_Target { return SpriteTex.Sample(SpriteSampler, uv); }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.ShouldContain("Texture2D SpriteTex : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState SpriteSampler : register(s0);", Case.Sensitive);
        // The comments are returned untouched, never given a register.
        result.ShouldContain("// ShadowDusk rewrites `texture T;` -> `Texture2D T;` so the resource exists.", Case.Sensitive);
        result.ShouldContain("/* Texture2D Block; SamplerState BlockSampler; Block.Sample(BlockSampler, uv) */", Case.Sensitive);
    }

    [Fact]
    public void Rewrite_SampleCallOnlyInAComment_DoesNotPairOrRaiseSD0028()
    {
        // A commented-out second Sample through the same sampler is not a live code path.
        const string hlsl = """
            Texture2D A;
            Texture2D B;
            SamplerState S;
            SamplerState T;

            float4 PS(float2 uv : TEXCOORD0) : SV_Target
            {
                // return B.Sample(S, uv);
                return A.Sample(S, uv) + B.Sample(T, uv);
            }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.ShouldContain("Texture2D A : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState S : register(s0);", Case.Sensitive);
        result.ShouldContain("Texture2D B : register(t1);", Case.Sensitive);
        result.ShouldContain("SamplerState T : register(s1);", Case.Sensitive);
    }
    [Fact]
    public void Rewrite_AHelperTakingTextureAndSampler_PairsWhatItsCallSitePasses()
    {
        const string hlsl = """
            Texture2D A;
            SamplerState S;
            Texture2D B;
            SamplerState U;

            float4 H(Texture2D t, SamplerState s, float2 uv) { return t.Sample(s, uv); }
            float4 PS(float2 uv : TEXCOORD0) : SV_Target { return A.Sample(S, uv) + H(B, U, uv); }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.ShouldContain("Texture2D B : register(t1);", Case.Sensitive);
        result.ShouldContain("SamplerState U : register(s1);", Case.Sensitive);
    }

    [Fact]
    public void Rewrite_AHelperSamplingAParameterTextureThroughAGlobalSampler_PairsThem()
    {
        const string hlsl = """
            Texture2D A;
            SamplerState S;
            Texture2D B;
            SamplerState U;

            float4 H(Texture2D t, float2 uv) { return t.Sample(U, uv); }
            float4 PS(float2 uv : TEXCOORD0) : SV_Target { return A.Sample(S, uv) + H(B, uv); }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.ShouldContain("Texture2D B : register(t1);", Case.Sensitive);
        result.ShouldContain("SamplerState U : register(s1);", Case.Sensitive);
    }

    [Fact]
    public void Rewrite_HelpersCallingHelpers_AreFollowed()
    {
        const string hlsl = """
            Texture2D B;
            SamplerState U;

            float4 Inner(Texture2D t, SamplerState s, float2 uv) { return t.Sample(s, uv); }
            float4 Outer(Texture2D t, SamplerState s, float2 uv) { return Inner(t, s, uv); }
            float4 PS(float2 uv : TEXCOORD0) : SV_Target { return Outer(B, U, uv); }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.ShouldContain("Texture2D B : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState U : register(s0);", Case.Sensitive);
    }

    [Fact]
    public void Rewrite_AHelperCalledWithTwoTexturesForOneSampler_IsSD0028()
    {
        // The same shape as two direct calls through one sampler: one combined descriptor
        // cannot serve two textures.
        const string hlsl = """
            Texture2D A;
            Texture2D B;
            SamplerState U;

            float4 H(Texture2D t, SamplerState s, float2 uv) { return t.Sample(s, uv); }
            float4 PS(float2 uv : TEXCOORD0) : SV_Target { return H(A, U, uv) + H(B, U, uv); }
            """;

        var act = () => VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        Should.Throw<VulkanSamplerSharingException>(act).Error.Code.ShouldBe("SD0028");
    }

    [Fact]
    public void Rewrite_AnExpressionArgument_OrAnOverloadedHelper_IsNotFollowed()
    {
        // Not unambiguous: `(U)` is an expression and `G` has two definitions. Neither pair is
        // made here (the compile then reports the sampler without a texture, SD0232, loudly).
        const string hlsl = """
            Texture2D B;
            SamplerState U;
            Texture2D C;
            SamplerState V;

            float4 H(Texture2D t, SamplerState s, float2 uv) { return t.Sample(s, uv); }
            float4 G(Texture2D t, SamplerState s, float2 uv) { return t.Sample(s, uv); }
            float4 G(Texture2D t, SamplerState s, float uv) { return t.Sample(s, float2(uv, uv)); }
            float4 PS(float2 uv : TEXCOORD0) : SV_Target { return H(B, (U), uv) + G(C, V, uv); }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.ShouldContain("Texture2D B : register(t0);", Case.Sensitive);
        result.ShouldContain("Texture2D C : register(t1);", Case.Sensitive);
        result.ShouldContain("SamplerState U : register(s2);", Case.Sensitive);
        result.ShouldContain("SamplerState V : register(s3);", Case.Sensitive);
    }

    // ---- Per compiled shader: reachability, skipped lines, co-live aliases ----------------

    private static string RewriteFor(string hlsl, string entryPoint, out IReadOnlyList<VulkanTextureSamplerBindingRewriter.Alias> aliases, params int[] skippedLines) =>
        VulkanTextureSamplerBindingRewriter.Rewrite(hlsl, entryPoint, skippedLines.Length == 0 ? null : skippedLines.ToHashSet(), out aliases);

    [Fact]
    public void Rewrite_ACallInAFunctionTheEntryPointNeverCalls_PairsNothing()
    {
        // The dead function's call came first in the file and used to give SpriteTexture its
        // sampler, moving the live pair off slot 0.
        const string hlsl = """
            Texture2D SpriteTexture; SamplerState U; SamplerState S;
            float4 Unused(float2 uv) { return SpriteTexture.Sample(U, uv); }
            float4 H(Texture2D t, SamplerState s, float2 uv) { return t.Sample(s, uv); }
            float4 AlsoUnused(float2 uv) { return H(SpriteTexture, U, uv); }
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target0 { return SpriteTexture.Sample(S, uv); }
            """;

        string result = RewriteFor(hlsl, "MainPS", out var aliases);

        result.ShouldContain("Texture2D SpriteTexture : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState S : register(s0);", Case.Sensitive);
        result.ShouldNotContain("_SDVkAlias", Case.Sensitive);
        aliases.ShouldBeEmpty();

        // The whole-file rewrite still reads the first call (no entry point to scope by).
        VulkanTextureSamplerBindingRewriter.Rewrite(hlsl).ShouldContain("SamplerState U : register(s0);", Case.Sensitive);
    }

    [Fact]
    public void Rewrite_AStructMethodTheEntryPointCalls_IsReachable()
    {
        // `h.Get(uv)` is a call: the method's Sample pairs SpriteTexture with S (it used to be skipped as
        // unreachable, leaving S a standalone sampler, SD0232, where the whole-file rewrite compiled it).
        const string hlsl = """
            Texture2D SpriteTexture; SamplerState U; SamplerState S;
            struct Helper { float4 Get(float2 uv) { return SpriteTexture.Sample(S, uv); } };
            float4 Unused(float2 uv) { return SpriteTexture.Sample(U, uv); }
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target0 { Helper h; return h.Get(uv); }
            """;

        string result = RewriteFor(hlsl, "MainPS", out var aliases);

        result.ShouldContain("Texture2D SpriteTexture : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState S : register(s0);", Case.Sensitive);
        aliases.ShouldBeEmpty();
    }

    [Fact]
    public void Rewrite_ATexturePerPassSampler_IsOneDescriptorAtItsSlotInEachShader()
    {
        // RegisterSamplerPerPass.fx: each compiled shader is its own descriptor layout, so the
        // second pass needs no alias (it used to read an alias on slot 1, which SpriteBatch never fills).
        const string hlsl = """
            Texture2D SpriteTex : register(t0); SamplerState PointS : register(s0); SamplerState LinearS : register(s1);
            float4 PS1(float2 uv : TEXCOORD0) : SV_Target0 { return SpriteTex.Sample(PointS, uv); }
            float4 PS2(float2 uv : TEXCOORD0) : SV_Target0 { return SpriteTex.Sample(LinearS, uv); }
            """;

        string second = RewriteFor(hlsl, "PS2", out var aliases);

        second.ShouldContain("SamplerState LinearS : register(s0);", Case.Sensitive);
        second.ShouldNotContain("_SDVkAlias", Case.Sensitive);
        aliases.ShouldBeEmpty();
    }

    [Fact]
    public void Rewrite_ACallOnASkippedLine_PairsNothingAndKeepsNoSamplerLive()
    {
        // '#if 0' around the first call: the live pairing is S, on slot 0 (it used to be an alias on
        // slot 1, away from SpriteBatch's texture). Line 4 is the one the preprocessor skips.
        const string hlsl = """
            Texture2D SpriteTexture; SamplerState S; SamplerState U : register(s0);
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target0 {
            #if 0
              return SpriteTexture.Sample(U, uv);
            #endif
              return SpriteTexture.Sample(S, uv);
            }
            """;

        string result = RewriteFor(hlsl, "MainPS", out var aliases, 4);

        result.ShouldContain("Texture2D SpriteTexture : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState S : register(s0);", Case.Sensitive);
        // U is named only on the skipped line, so it is dead and its s0 reserves nothing.
        result.ShouldNotContain("SamplerState U : register(s0);", Case.Sensitive);
        aliases.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void Rewrite_SkippedLines_AreNumberedAsThePreprocessorNumbersThem(string newline)
    {
        // FxMacroPreprocessor ends a line at a lone '\r' too, so its skipped line 4 is the '#if 0'
        // call here whatever the line breaks are.
        string hlsl = string.Join(newline,
            "Texture2D SpriteTexture; SamplerState S; SamplerState U;",
            "float4 MainPS(float2 uv : TEXCOORD0) : SV_Target0 {",
            "#if 0",
            "  return SpriteTexture.Sample(U, uv);",
            "#endif",
            "  return SpriteTexture.Sample(S, uv);",
            "}");

        string result = RewriteFor(hlsl, "MainPS", out var aliases, 4);

        result.ShouldContain("SamplerState S : register(s0);", Case.Sensitive);
        result.ShouldNotContain("_SDVkAlias", Case.Sensitive);
        aliases.ShouldBeEmpty();
    }

    [Fact]
    public void Rewrite_ASecondTextureOnASkippedLinesSampler_IsNotSD0028()
    {
        // A through U only in '#if 0' (line 4); live: A through S, B through U.
        const string hlsl = """
            Texture2D A; Texture2D B; SamplerState S; SamplerState U;
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target0 {
            #if 0
             return A.Sample(U, uv);
            #endif
             return A.Sample(S, uv) + B.Sample(U, uv); }
            """;

        string result = RewriteFor(hlsl, "MainPS", out _, 4);

        result.ShouldContain("Texture2D A : register(t0);", Case.Sensitive);
        result.ShouldContain("SamplerState S : register(s0);", Case.Sensitive);
        result.ShouldContain("Texture2D B : register(t1);", Case.Sensitive);
        result.ShouldContain("SamplerState U : register(s1);", Case.Sensitive);
    }

    [Fact]
    public void Rewrite_SecondSamplerInTheOtherBranchOfAnIf_IsNotAliased()
    {
        // Mutually exclusive branches: only one call survives the compile, so the second sampler is
        // resolved by the preprocessed retry, not given an alias that would be the only live texture.
        const string hlsl = """
            Texture2D SpriteTexture; SamplerState PointS; SamplerState LinearS;
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target0 {
            #ifdef POINT_FILTER
              return SpriteTexture.Sample(PointS, uv);
            #else
              return SpriteTexture.Sample(LinearS, uv);
            #endif
            }
            """;

        string result = RewriteFor(hlsl, "MainPS", out var aliases);

        result.ShouldNotContain("_SDVkAlias", Case.Sensitive);
        aliases.ShouldBeEmpty();
    }

    [Fact]
    public void Rewrite_ACoLiveSecondSampler_DeclaresAnAliasAndReportsIt()
    {
        const string hlsl = """
            Texture2D SpriteTexture; SamplerState S1; SamplerState S2;
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target0 { return SpriteTexture.Sample(S1, uv) + SpriteTexture.Sample(S2, uv); }
            """;

        string result = RewriteFor(hlsl, "MainPS", out var aliases);

        result.ShouldContain("Texture2D SpriteTexture : register(t0); Texture2D SpriteTexture_SDVkAlias1 : register(t1);", Case.Sensitive);
        result.ShouldContain("SpriteTexture_SDVkAlias1.Sample(S2, uv)", Case.Sensitive);
        var alias = aliases.ShouldHaveSingleItem();
        (alias.Texture, alias.Sampler, alias.Name).ShouldBe(("SpriteTexture", "S2", "SpriteTexture_SDVkAlias1"));
        result.Substring(alias.Offset).ShouldStartWith("SpriteTexture_SDVkAlias1.Sample(S2", Case.Sensitive);
    }

    [Fact]
    public void Rewrite_AnUppercaseRegisterLetter_IsWrittenLowercase()
    {
        // DXC's SPIR-V backend rejects `register(T1)` with an internal compiler error (X0000, no
        // line) that its DXIL backend and vkd3d accept.
        const string hlsl = """
            Texture2D A : register(T1);
            SamplerState S : register(S1, SPACE0);
            cbuffer P : register(B0) { float4 Tint; };
            RWTexture2D<float4> W : register(U2, space0);

            float4 PS(float2 uv : TEXCOORD0) : SV_Target { return A.Sample(S, uv) * Tint; }
            """;

        string result = VulkanTextureSamplerBindingRewriter.Rewrite(hlsl);

        result.ShouldContain("Texture2D A : register(t1);", Case.Sensitive);
        result.ShouldContain("SamplerState S : register(s1);", Case.Sensitive);
        result.ShouldContain("cbuffer P : register(b0) {", Case.Sensitive);
        result.ShouldContain("RWTexture2D<float4> W : register(u2);", Case.Sensitive);
    }
}