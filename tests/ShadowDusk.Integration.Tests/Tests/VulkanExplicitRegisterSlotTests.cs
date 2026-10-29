#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// An explicit <c>register(tN)</c>/<c>register(sN)</c> is the resource's slot on Vulkan, as it is on
/// DirectX 11 and DirectX 12. mgfxc 3.8.5 <c>/Profile:Vulkan</c> writes the slot as the SPIR-V binding
/// minus 32 (the <c>-fvk-t-shift</c>/<c>-fvk-s-shift</c> amount), and MonoGame's DesktopVK runtime
/// reads a descriptor's image from <c>textures[binding - 32]</c> while binding a texture parameter to
/// <c>GraphicsDevice.Textures[record.TextureSlot]</c>.
///
/// <para>ShadowDusk used to write the texture's ORDINAL among the stage's textures, which is the same
/// number only when the registers start at 0 with no gap. A lone <c>Texture2D Tex1 : register(t1)</c>
/// got slot 0 (and header texture-slot mask <c>0x1</c>, where mgfxc writes <c>0x2</c>), and in real
/// MonoGame 3.8.5 DesktopVK it drew (0,0,0,0) where mgfxc's build draws the texture; a
/// <c>Texture2DArray</c> on <c>t1</c> likewise. Render row: <c>validation/VsDrivenVulkan --
/// regslots</c>.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "Vulkan")]
public sealed class VulkanExplicitRegisterSlotTests
{
    private const uint CombinedImageSampler = 1;
    private const uint SampledImage = 2;
    private const uint SamplerOnly = 0;

    // RegisterSpace0 and RegisterDeadSampler: the golden is mgfxc on the shape as MonoGame can run it
    // (spaces removed; the DirectX-equivalent t0/s0), because mgfxc's build of the file as written
    // draws nothing (see each fixture's header).
    // RegisterHelperPair and RegisterPreprocessedPairs likewise (mgfxc on the explicit-register form);
    // RegisterLoadOnly is mgfxc on the file itself.
    public static TheoryData<string> GoldenMatched() => new()
    {
        "RegisterLoneT1", "RegisterGapT0T2", "RegisterTexture2DArrayT1", "RegisterSpace0", "RegisterDeadSampler",
        "RegisterLoadOnly", "RegisterHelperPair", "RegisterPreprocessedPairs",
    };

    public static TheoryData<string> AllShapes() => new()
    {
        "RegisterLoneT1", "RegisterGapT0T2", "RegisterMismatchedTS", "RegisterTexture2DArrayT1", "RegisterSpace0", "RegisterDeadSampler",
        "RegisterLoadOnly", "RegisterHelperPair", "RegisterPreprocessedPairs", "RegisterSamplerPerPass", "RegisterSamplerPerStage",
    };

    [Theory]
    [MemberData(nameof(GoldenMatched))]
    public async Task Vulkan_SlotRecordsAndDescriptorHeader_EqualTheMgfxcGolden(string stem)
    {
        var subject = MgfxBlobReader.Parse(await CompileAsync(stem, PlatformTarget.Vulkan));
        var golden  = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(GoldenPath(stem)));

        // Record for record: the slots and the parameter each record binds (by name: ShadowDusk's
        // Vulkan table also lists the sampler parameters, so the indices themselves differ).
        subject.Samplers.Count.ShouldBe(golden.Samplers.Count);
        for (int i = 0; i < golden.Samplers.Count; i++)
        {
            MgfxSamplerRecord want = golden.Samplers[i], got = subject.Samplers[i];
            got.TextureSlot.ShouldBe(want.TextureSlot, $"record {i} texture slot");
            got.SamplerSlot.ShouldBe(want.SamplerSlot, $"record {i} sampler slot");
            got.Type.ShouldBe(want.Type, $"record {i} sampler type");
            subject.ParameterNames[got.Parameter].ShouldBe(golden.ParameterNames[want.Parameter], $"record {i} parameter");
        }

        var gotHeader  = VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode);
        var wantHeader = VulkanShaderCodeReader.Parse(golden.Shaders.Single().Bytecode);
        gotHeader.TextureSlots.ShouldBe(wantHeader.TextureSlots, "texture slot mask");
        gotHeader.SamplerSlots.ShouldBe(wantHeader.SamplerSlots, "sampler slot mask");
        gotHeader.TextureTypes.ShouldBe(wantHeader.TextureTypes, "texture type per slot");
        gotHeader.Bindings.Select(b => (b.Binding, b.DescriptorType))
            .ShouldBe(wantHeader.Bindings.Select(b => (b.Binding, b.DescriptorType)), "descriptor bindings");
    }

    [Fact]
    public async Task Vulkan_LoneRegisterT1_IsSlot1()
    {
        var subject = MgfxBlobReader.Parse(await CompileAsync("RegisterLoneT1", PlatformTarget.Vulkan));

        MgfxSamplerRecord record = subject.Samplers.ShouldHaveSingleItem();
        record.TextureSlot.ShouldBe((byte)1);
        record.SamplerSlot.ShouldBe((byte)1);
        subject.ParameterNames[record.Parameter].ShouldBe("Tex1");

        var header = VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode);
        header.TextureSlots.ShouldBe(0x2u);
        header.SamplerSlots.ShouldBe(0x2u);
        header.Bindings.Single().Binding.ShouldBe(33u);
    }

    [Fact]
    public async Task Vulkan_RegisterGap_KeepsSlot2()
    {
        var subject = MgfxBlobReader.Parse(await CompileAsync("RegisterGapT0T2", PlatformTarget.Vulkan));

        subject.Samplers.Select(s => (subject.ParameterNames[s.Parameter], s.TextureSlot, s.SamplerSlot))
            .ShouldBe([("Base", (byte)0, (byte)0), ("Overlay", (byte)2, (byte)2)]);
        VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode).TextureSlots.ShouldBe(0x5u);
    }

    [Fact]
    public async Task Vulkan_Texture2DArrayOnT1_IsSlot1()
    {
        var subject = MgfxBlobReader.Parse(await CompileAsync("RegisterTexture2DArrayT1", PlatformTarget.Vulkan));

        MgfxSamplerRecord record = subject.Samplers.ShouldHaveSingleItem();
        record.TextureSlot.ShouldBe((byte)1);
        subject.ParameterNames[record.Parameter].ShouldBe("Arr");
        VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode).TextureSlots.ShouldBe(0x2u);
    }

    [Fact]
    public async Task Vulkan_MismatchedTextureAndSamplerRegisters_TakeTheTextureRegister()
    {
        // register(t1) read through register(s3). MonoGame's Vulkan descriptor model is one
        // COMBINED_IMAGE_SAMPLER per texture, so the binding rewrite moves the sampler onto the
        // texture's register and the pair is one descriptor at binding 33, slot 1.
        var subject = MgfxBlobReader.Parse(await CompileAsync("RegisterMismatchedTS", PlatformTarget.Vulkan));

        MgfxSamplerRecord record = subject.Samplers.ShouldHaveSingleItem();
        record.TextureSlot.ShouldBe((byte)1);
        record.SamplerSlot.ShouldBe((byte)1);
        var header = VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode);
        header.Bindings.Select(b => (b.Binding, b.DescriptorType)).ShouldBe([(33u, CombinedImageSampler)]);

        // mgfxc agrees on the texture slot; it keeps the registers apart (an image at 33 and a
        // sampler at 35), the separate SAMPLED_IMAGE + SAMPLER shape MonoGame's native Vulkan
        // descriptor writer does not handle (measured from the committed golden).
        var golden = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(GoldenPath("RegisterMismatchedTS")));
        MgfxSamplerRecord goldenRecord = golden.Samplers.ShouldHaveSingleItem();
        goldenRecord.TextureSlot.ShouldBe(record.TextureSlot);
        goldenRecord.SamplerSlot.ShouldBe((byte)3);
        VulkanShaderCodeReader.Parse(golden.Shaders.Single().Bytecode).Bindings
            .Select(b => (b.Binding, b.DescriptorType)).OrderBy(b => b.Binding)
            .ShouldBe([(33u, SampledImage), (35u, SamplerOnly)]);
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public async Task TextureSlots_AreTheSameOnVulkanDirectX11AndDirectX12(string stem)
    {
        // DirectX is the reference for what a register means: a texture parameter lands on the
        // slot its register names, on every target.
        var vulkan = TextureSlots(MgfxBlobReader.Parse(await CompileAsync(stem, PlatformTarget.Vulkan)));
        var dx12   = TextureSlots(MgfxBlobReader.Parse(await CompileAsync(stem, PlatformTarget.DirectX12)));
        var dx11   = TextureSlots(MgfxBlobReader.Parse(await CompileAsync(stem, PlatformTarget.DirectX)));

        vulkan.ShouldBe(dx12);
        vulkan.ShouldBe(dx11);
    }

    [Fact]
    public async Task ExLegacyTextureDiscard_SpriteSamplerIsSlot0_LikeDirectX()
    {
        // `sampler s0;` is the sprite SpriteBatch binds at slot 0; the mask is the second texture.
        // A commented `Texture2D T;` in the file's header used to be collected as a texture and
        // pushed both to t1/t2 on Vulkan (the binding rewriter now ignores comments).
        var subject = MgfxBlobReader.Parse(await CompileAsync(Path.Combine("examples", "ExLegacyTextureDiscard"), PlatformTarget.Vulkan));

        subject.Samplers.Select(s => (subject.ParameterNames[s.Parameter], s.TextureSlot))
            .ShouldBe([("s0", (byte)0), ("_maskTex", (byte)1)]);
        VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode).Bindings
            .Where(b => b.DescriptorType == CombinedImageSampler).Select(b => b.Binding)
            .ShouldBe([32u, 33u]);
    }

    [Fact]
    public async Task AnUnpinnedTextureDeclaration_IsRefusedWithSD0228()
    {
        // `Texture2D A, B;` is a form the Vulkan binding rewrite does not model, so DXC numbers A
        // from binding 0 and binding - 32 has no slot. mgfxc 3.8.5 writes such a texture as slot
        // 224 with no descriptor binding at all (measured); ShadowDusk refuses it by name.
        const string source = """
            #if SM6
            #define PS_SHADERMODEL ps_6_0
            #else
            #define PS_SHADERMODEL ps_4_0
            #endif

            Texture2D A, B;
            SamplerState S : register(s0);

            float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
            {
                return A.Sample(S, uv) * color;
            }

            technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
            """;
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "Unpinned.fx",
        }, cts.Token);

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0228");
        error.File.ShouldBe("Unpinned.fx");
        error.Line.ShouldBe(7);
        error.Column.ShouldBe("Texture2D ".Length + 1);
        error.Message.ShouldContain("texture 'A'", Case.Sensitive);
    }

    [Fact]
    public async Task AMacroDeclaredTexture_IsPinnedThroughThePreprocessedRetry()
    {
        // The declaration comes out of a macro, so the binding rewrite cannot see it on the raw
        // text and DXC numbers it at binding 0, apart from its sampler (the separate
        // SAMPLED_IMAGE + SAMPLER shape MonoGame's Vulkan descriptor writer cannot bind). The first
        // attempt's SD0228 triggers one compile from the preprocessed source, where the
        // declaration is literal: one combined descriptor at binding 32, slot 0, as on DirectX 12.
        const string source = """
            #if SM6
            #define PS_SHADERMODEL ps_6_0
            #else
            #define PS_SHADERMODEL ps_4_0_level_9_1
            #endif
            #define DECL_TEX(x) Texture2D x##Tex;
            DECL_TEX(Mask)
            sampler2D A = sampler_state { Texture = <MaskTex>; };
            float4 MainPS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) * color; }
            technique Main { pass P0 { PixelShader = compile PS_SHADERMODEL MainPS(); } };
            """;
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "MacroTex.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        var reader = MgfxBlobReader.Parse(result.Value.Data);
        MgfxSamplerRecord record = reader.Samplers.ShouldHaveSingleItem();
        record.TextureSlot.ShouldBe((byte)0);
        reader.ParameterNames[record.Parameter].ShouldBe("MaskTex");
        VulkanShaderCodeReader.Parse(reader.Shaders.Single().Bytecode).Bindings
            .Select(b => (b.Binding, b.DescriptorType)).ShouldBe([(32u, CombinedImageSampler)]);
    }

    [Fact]
    public async Task Vulkan_AnExplicitRegisterSpace_IsDropped_SoEveryResourceIsInThePixelStagesSet()
    {
        // register(t1, space0) / (s1, space0) / (b0, space0) in a pixel shader: mgfxc 3.8.5 keeps
        // DescriptorSet 0 (measured from its build of this file) and that build draws (0,0,0,0) in
        // DesktopVK, which binds a pixel shader's resources from set 1 only.
        var subject = MgfxBlobReader.Parse(await CompileAsync("RegisterSpace0", PlatformTarget.Vulkan));
        var header = VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode);

        var reflected = new ShadowDusk.Core.Reflection.SpirvReflector().Reflect(header.Spirv).Value;
        reflected.Textures.ShouldHaveSingleItem().RawDescriptorSet.ShouldBe(1);
        reflected.Samplers.ShouldHaveSingleItem().RawDescriptorSet.ShouldBe(1);
        reflected.ConstantBuffers.ShouldHaveSingleItem().RawDescriptorSet.ShouldBe(1);
        subject.Samplers.ShouldHaveSingleItem().TextureSlot.ShouldBe((byte)1);
    }

    [Fact]
    public async Task Vulkan_ARegisterSpaceFromAMacro_IsDroppedThroughThePreprocessedRetry()
    {
        // The rewrite cannot see `space0` behind a macro on the raw text, so the first attempt puts
        // the texture in set 0 (SD0229); the retry from the preprocessed source drops it.
        const string source = """
            #if SM6
            #define PS_SHADERMODEL ps_6_0
            #else
            #define PS_SHADERMODEL ps_4_0
            #endif
            #define SPACE space0
            Texture2D Tex : register(t1, SPACE);
            SamplerState TexSampler : register(s1, SPACE);
            float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0 { return Tex.Sample(TexSampler, uv) * color; }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
            """;
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "MacroSpace.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        var reader = MgfxBlobReader.Parse(result.Value.Data);
        reader.Samplers.ShouldHaveSingleItem().TextureSlot.ShouldBe((byte)1);
        var spirv = VulkanShaderCodeReader.Parse(reader.Shaders.Single().Bytecode).Spirv;
        new ShadowDusk.Core.Reflection.SpirvReflector().Reflect(spirv).Value.Textures.ShouldHaveSingleItem().RawDescriptorSet.ShouldBe(1);
    }

    [Fact]
    public async Task SD0228AfterTheRetry_NamesOnlyWhatTheRetryCouldNotPin_AtItsLine()
    {
        // MainTex comes out of a macro (the retry pins it); `Texture2D A, B;` is a form neither
        // pass models. The error must name A, at its line, not the macro-declared MainTex.
        const string source = """
            #if SM6
            #define PS_SHADERMODEL ps_6_0
            #else
            #define PS_SHADERMODEL ps_4_0
            #endif
            #define DECL_TEX(x) Texture2D x##Tex;
            DECL_TEX(Main)
            Texture2D A, B;
            SamplerState S;
            SamplerState S2;
            float4 MainPS(float4 p : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 { return MainTex.Sample(S, uv) + A.Sample(S2, uv); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
            """;
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "MacroThenMulti.fx",
        }, cts.Token);

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0228");
        error.Message.ShouldContain("texture 'A' (binding", Case.Sensitive);
        error.Message.ShouldNotContain("MainTex", Case.Sensitive);
        error.File.ShouldBe("MacroThenMulti.fx");
        error.Line.ShouldBe(8);
        error.Column.ShouldBe("Texture2D ".Length + 1);
    }

    [Fact]
    public async Task ASamplerThePairingCannotFollow_IsRefusedWithSD0232_AfterThePreprocessedRetry()
    {
        // It used to ship a standalone SAMPLER descriptor next to a SAMPLED_IMAGE, silently: the
        // shape MonoGame's MGVK_UpdateDescriptors does not handle.
        const string source = """
            #if SM6
            #define PS_SHADERMODEL ps_6_0
            #else
            #define PS_SHADERMODEL ps_4_0
            #endif
            Texture2D A;
            SamplerState S;
            Texture2D B;
            SamplerState U;
            float4 H(Texture2D t, SamplerState s, float2 uv) { return t.Sample(s, uv); }
            float4 MainPS(float4 p : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 { return A.Sample(S, uv) + H(B, (U), uv); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
            """;
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions { Target = PlatformTarget.Vulkan, SourceFileName = "Unpaired.fx" }, cts.Token);

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0232");
        error.Message.ShouldContain("sampler 'U' has no texture on its descriptor binding", Case.Sensitive);
        error.Message.ShouldContain("'B'", Case.Sensitive);
        error.File.ShouldBe("Unpaired.fx");
        error.Line.ShouldBe(9);
    }

    [Theory]
    [InlineData("SamplerState S : register(S1);\nTexture2D A : register(T1);")]
    [InlineData("SamplerState S : register(s1, SPACE0);\nTexture2D A : register(T1, space0);")]
    public async Task Vulkan_AnUppercaseRegisterLetter_CompilesToItsSlot_AsOnDirectX12(string declarations)
    {
        // DXC's SPIR-V backend used to fail with an internal compiler error (X0000, no line).
        string source = "#if SM6\n#define PS_SHADERMODEL ps_6_0\n#else\n#define PS_SHADERMODEL ps_4_0\n#endif\n" + declarations + "\n" +
                        "float4 MainPS(float4 p : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 { return A.Sample(S, uv); }\n" +
                        "technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n";
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var vulkan = await new EffectCompiler().CompileAsync(source, new CompilerOptions { Target = PlatformTarget.Vulkan, SourceFileName = "Upper.fx" }, cts.Token);
        var dx12 = await new EffectCompiler().CompileAsync(source, new CompilerOptions { Target = PlatformTarget.DirectX12, SourceFileName = "Upper.fx" }, cts.Token);

        vulkan.IsSuccess.ShouldBeTrue(vulkan.IsFailure ? string.Join("; ", vulkan.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        MgfxBlobReader.Parse(vulkan.Value.Data).Samplers.ShouldHaveSingleItem().TextureSlot.ShouldBe((byte)1);
        if (dx12.IsSuccess)
            MgfxBlobReader.Parse(dx12.Value.Data).Samplers.ShouldHaveSingleItem().TextureSlot.ShouldBe((byte)1);
    }

    [Fact]
    public async Task Vulkan_AReadWriteTexture_IsRefusedWithSD0233_AtItsDeclaration()
    {
        const string source = """
            #if SM6
            #define PS_SHADERMODEL ps_6_0
            #else
            #define PS_SHADERMODEL ps_4_0
            #endif
            Texture2D A : register(t0);
            SamplerState S : register(s0);
            RWTexture2D<float4> W : register(u1, space0);
            float4 MainPS(float4 p : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 { W[uint2(0, 0)] = 1; return A.Sample(S, uv); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
            """;
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions { Target = PlatformTarget.Vulkan, SourceFileName = "Uav.fx" }, cts.Token);

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0233");
        error.Message.ShouldContain("'W' is a read-write resource", Case.Sensitive);
        error.Line.ShouldBe(8);
        error.Column.ShouldBe("RWTexture2D<float4> ".Length + 1);
    }

    [Fact]
    public async Task Vulkan_OneTextureThroughTwoSamplers_GetsTwoCombinedDescriptorsBoundToTheOneParameter()
    {
        // The linear + point idiom: Tex1 through LinearSampler and PointSampler. The second
        // pairing gets an alias of Tex1 on its own register; its record binds the Tex1 parameter
        // and the alias is not a parameter of its own. Same layout as mgfxc's two-texture form.
        CompiledShader compiled = await CompileShaderAsync("RegisterTwoSamplers", PlatformTarget.Vulkan);
        var subject = MgfxBlobReader.Parse(compiled.Data);
        var golden  = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(GoldenPath("RegisterTwoSamplers")));

        subject.ParameterNames.ShouldNotContain(n => n.Contains("_SDVkAlias", StringComparison.Ordinal));
        subject.Samplers.Select(s => (subject.ParameterNames[s.Parameter], s.TextureSlot, s.SamplerSlot))
            .ShouldBe([("SpriteTex", (byte)0, (byte)0), ("Tex1", (byte)1, (byte)1), ("Tex1", (byte)2, (byte)2)]);

        // Three COMBINED_IMAGE_SAMPLER descriptors at 32..34, never a standalone sampler (mgfxc's own
        // build of the one-texture form), and the same layout as mgfxc's two-texture form.
        var gotHeader  = VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode);
        var wantHeader = VulkanShaderCodeReader.Parse(golden.Shaders.Single().Bytecode);
        gotHeader.Bindings.Select(b => (b.Binding, b.DescriptorType))
            .ShouldBe([(32u, CombinedImageSampler), (33u, CombinedImageSampler), (34u, CombinedImageSampler)]);
        gotHeader.TextureSlots.ShouldBe(wantHeader.TextureSlots);
        gotHeader.SamplerSlots.ShouldBe(wantHeader.SamplerSlots);
        gotHeader.Bindings.Select(b => (b.Binding, b.DescriptorType))
            .ShouldBe(wantHeader.Bindings.Select(b => (b.Binding, b.DescriptorType)));

        // The alias is announced: SpriteBatch fills slot 0 only, so the texture must be set by parameter.
        ShaderError warning = compiled.Warnings.Where(w => w.Code == "SD0235").ShouldHaveSingleItem();
        warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warning.Message.ShouldContain("texture 'Tex1' is sampled through a second sampler, 'PointSampler', in shader 'MainPS'", Case.Sensitive);
        warning.Message.ShouldContain("Effect.Parameters", Case.Sensitive);
        warning.Line.ShouldBe(38);
    }

    // ---- One texture, a different sampler per compiled shader -----------------------------------

    public static TheoryData<string> PerShaderSamplerShapes() => new() { "RegisterSamplerPerPass", "RegisterSamplerPerStage" };

    [Theory]
    [MemberData(nameof(PerShaderSamplerShapes))]
    public async Task Vulkan_ATextureReadThroughADifferentSamplerPerShader_IsOneCombinedDescriptorAtItsSlotInEach(string stem)
    {
        // Each compiled shader is its own descriptor layout: the binding rewrite pairs per shader, so
        // there is no alias and every shader reads the texture at slot 0 (where SpriteBatch binds the
        // sprite). The golden is mgfxc 3.8.5 on the one-sampler form (see the fixture header).
        CompiledShader compiled = await CompileShaderAsync(stem, PlatformTarget.Vulkan);
        var subject = MgfxBlobReader.Parse(compiled.Data);
        var golden  = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(GoldenPath(stem)));

        compiled.Warnings.ShouldNotContain(w => w.Code == "SD0235");
        subject.ParameterNames.ShouldNotContain(n => n.Contains("_SDVkAlias", StringComparison.Ordinal));

        // Shader by shader, matched by stage and order within it (mgfxc lists the pixel shader first).
        static List<(bool IsVertex, byte TextureSlot, byte SamplerSlot, string Parameter, IReadOnlyList<(uint, uint)> Bindings)> PerShader(MgfxBlobReader r) =>
            r.Shaders.OrderBy(s => s.IsVertex).ThenBy(s => s.Index)
                .SelectMany(s => r.Samplers.Where(x => x.ShaderIndex == s.Index).Select(x => (
                    s.IsVertex, x.TextureSlot, x.SamplerSlot, r.ParameterNames[x.Parameter],
                    (IReadOnlyList<(uint, uint)>)VulkanShaderCodeReader.Parse(s.Bytecode).Bindings
                        .Where(b => b.Binding >= 32).Select(b => (b.Binding, b.DescriptorType)).ToList())))
                .ToList();

        var got = PerShader(subject);
        var want = PerShader(golden);
        got.Count.ShouldBe(want.Count);
        for (int i = 0; i < want.Count; i++)
        {
            (got[i].IsVertex, got[i].TextureSlot, got[i].SamplerSlot, got[i].Parameter)
                .ShouldBe((want[i].IsVertex, want[i].TextureSlot, want[i].SamplerSlot, want[i].Parameter), $"record {i}");
            got[i].Bindings.ShouldBe(want[i].Bindings, $"record {i} descriptor bindings");
            got[i].Bindings.ShouldBe([(32u, CombinedImageSampler)], $"record {i}: one combined descriptor at slot 0");
        }
    }

    [Fact]
    public async Task Vulkan_ASampleCallInIf0_DoesNotDecideTheSampler()
    {
        // The '#if 0' call came first and used to pair SpriteTexture with U, which DXC then removed,
        // leaving the live pairing on an alias at slot 1, away from SpriteBatch's slot 0.
        const string source = """
            #if SM6
            #define PS_SHADERMODEL ps_6_0
            #else
            #define PS_SHADERMODEL ps_4_0
            #endif
            Texture2D SpriteTexture; SamplerState S; SamplerState U;
            float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0 {
            #if 0
              return SpriteTexture.Sample(U, uv);
            #endif
              return SpriteTexture.Sample(S, uv) * color;
            }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
            """;
        var result = await CompileSourceAsync(source, "If0.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        var subject = MgfxBlobReader.Parse(result.Value.Data);
        MgfxSamplerRecord record = subject.Samplers.ShouldHaveSingleItem();
        (subject.ParameterNames[record.Parameter], record.TextureSlot, record.SamplerSlot).ShouldBe(("SpriteTexture", (byte)0, (byte)0));
        VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode).Bindings
            .Select(b => (b.Binding, b.DescriptorType)).ShouldBe([(32u, CombinedImageSampler)]);
        result.Value.Warnings.ShouldNotContain(w => w.Code == "SD0235");
    }

    // The third review's BLOCKER: a texture whose FIRST textual pairing is dead code. The whole-file
    // rewrite paired SpriteTexture with U (the dead call), aliased the live S pairing, and DXC then
    // removed the original, so the compile failed with the writer's internal SD0022 (the sampler
    // record named a parameter that no longer existed). Each shape must now compile to the one
    // combined descriptor at slot 0, bound to SpriteTexture, with no alias and no SD0235.
    public static TheoryData<string, string> DeadFirstPairings() => new()
    {
        {
            "uncalled function",
            "float4 Unused(float2 uv) { return SpriteTexture.Sample(U, uv); }\n"
        },
        {
            "uncalled helper call",
            "float4 H(Texture2D t, SamplerState s, float2 uv) { return t.Sample(s, uv); }\n" +
            "float4 Unused(float2 uv) { return H(SpriteTexture, U, uv); }\n"
        },
        {
            "inactive #ifdef",
            "#ifdef NOT_DEFINED_ANYWHERE\nfloat4 Legacy(float2 uv) { return SpriteTexture.Sample(U, uv); }\n#endif\n"
        },
        {
            "inactive #if inside the entry point",
            "#define USE_POINT 0\n"
        },
    };

    [Theory]
    [MemberData(nameof(DeadFirstPairings))]
    public async Task Vulkan_ATexturesFirstPairingInDeadCode_CompilesToOneDescriptorAtSlot0_NotSD0022(string shape, string deadCode)
    {
        bool insideEntry = shape == "inactive #if inside the entry point";
        string source =
            "#if SM6\n#define PS_SHADERMODEL ps_6_0\n#else\n#define PS_SHADERMODEL ps_4_0\n#endif\n" +
            "Texture2D SpriteTexture; SamplerState U; SamplerState S;\n" +
            deadCode +
            "float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0 {\n" +
            (insideEntry ? "#if USE_POINT\n  return SpriteTexture.Sample(U, uv);\n#endif\n" : "") +
            "  return SpriteTexture.Sample(S, uv) * color;\n}\n" +
            "technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n";

        var result = await CompileSourceAsync(source, "DeadFirst.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{shape}: " + string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        var subject = MgfxBlobReader.Parse(result.Value.Data);
        subject.ParameterNames.ShouldNotContain(n => n.Contains("_SDVkAlias", StringComparison.Ordinal));
        MgfxSamplerRecord record = subject.Samplers.ShouldHaveSingleItem();
        (subject.ParameterNames[record.Parameter], record.TextureSlot, record.SamplerSlot).ShouldBe(("SpriteTexture", (byte)0, (byte)0), shape);
        VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode).Bindings
            .Select(b => (b.Binding, b.DescriptorType)).ShouldBe([(32u, CombinedImageSampler)], shape);
        result.Value.Warnings.ShouldNotContain(w => w.Code == "SD0235");
    }

    [Fact]
    public async Task Vulkan_ASecondTextureOnASamplerOnlyAnIf0CallShares_IsNotSD0028()
    {
        const string source = """
            #if SM6
            #define PS_SHADERMODEL ps_6_0
            #else
            #define PS_SHADERMODEL ps_5_0
            #endif
            Texture2D A; Texture2D B; SamplerState S; SamplerState U;
            float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0 {
            #if 0
             return A.Sample(U, uv);
            #endif
             return A.Sample(S, uv) + B.Sample(U, uv); }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
            """;
        var result = await CompileSourceAsync(source, "If0Shared.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        var subject = MgfxBlobReader.Parse(result.Value.Data);
        subject.Samplers.Select(s => (subject.ParameterNames[s.Parameter], s.TextureSlot, s.SamplerSlot))
            .ShouldBe([("A", (byte)0, (byte)0), ("B", (byte)1, (byte)1)]);
    }

    [Fact]
    public async Task Vulkan_AnAliasWhoseOriginalDXCRemoves_IsBoundToTheOriginalsParameterName()
    {
        // Both calls are co-live to the rewrite (no #if between them), so B's pairing gets an alias;
        // DXC then removes the A call. The alias is the shader's only Tex1 and keeps the Tex1 name.
        const string source = """
            #if SM6
            #define PS_SHADERMODEL ps_6_0
            #else
            #define PS_SHADERMODEL ps_4_0
            #endif
            Texture2D Tex1; SamplerState A; SamplerState B;
            float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0 {
              [branch] if (false) return Tex1.Sample(A, uv);
              return Tex1.Sample(B, uv) * color;
            }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
            """;
        var result = await CompileSourceAsync(source, "AliasOnly.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        var subject = MgfxBlobReader.Parse(result.Value.Data);
        subject.ParameterNames.ShouldContain("Tex1");
        subject.ParameterNames.ShouldNotContain(n => n.Contains("_SDVkAlias", StringComparison.Ordinal));
        subject.ParameterNames[subject.Samplers.ShouldHaveSingleItem().Parameter].ShouldBe("Tex1");
    }

    [Fact]
    public async Task Vulkan_AnIdentifierWithTheAliasSuffix_IsRefusedWithSD0234_AndIsAnOrdinaryNameElsewhere()
    {
        const string source = """
            #if OPENGL
            #define PS_SHADERMODEL ps_3_0
            #elif SM6
            #define PS_SHADERMODEL ps_6_0
            #else
            #define PS_SHADERMODEL ps_4_0
            #endif
            Texture2D Tex1_SDVkAlias1; SamplerState S;
            float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0 { return Tex1_SDVkAlias1.Sample(S, uv) * color; }
            technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
            """;

        var vulkan = await CompileSourceAsync(source, "Reserved.fx");
        vulkan.IsFailure.ShouldBeTrue();
        ShaderError error = vulkan.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0234");
        error.Message.ShouldContain("'Tex1_SDVkAlias1'", Case.Sensitive);
        (error.File, error.Line, error.Column).ShouldBe(("Reserved.fx", 8, "Texture2D ".Length + 1));

        // Only Vulkan declares aliases, so on OpenGL the name is the author's own parameter.
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var gl = await new EffectCompiler().CompileAsync(source, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Reserved.fx" }, cts.Token);
        gl.IsSuccess.ShouldBeTrue(gl.IsFailure ? string.Join("; ", gl.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        MgfxBlobReader.Parse(gl.Value.Data).ParameterNames.ShouldContain("Tex1_SDVkAlias1");
    }

    [Theory]
    [InlineData("StructuredBuffer<float4> Buf : register(t1);", "Buf[0]", "'Buf' is a storage buffer")]
    [InlineData("RWStructuredBuffer<float4> Buf : register(u1);", "Buf[0]", "'Buf' is a storage buffer")]
    [InlineData("ByteAddressBuffer Buf : register(t1);", "asfloat(Buf.Load4(0))", "'Buf' is a storage buffer")]
    [InlineData("Buffer<float4> Buf;", "Buf[0]", "'Buf' is a typed buffer (a Buffer<T>, a texel buffer)")]
    [InlineData("RWBuffer<float4> Buf : register(u1);", "Buf[0]", "'Buf' is a read-write typed buffer")]
    public async Task Vulkan_ABufferResource_IsRefusedWithSD0233_AtItsDeclaration(string declaration, string read, string expected)
    {
        // A storage buffer used to compile silently to a UNIFORM_BUFFER descriptor the runtime never
        // fills, and a Buffer<T> compiled silently too (the first fix refused it as SD0228, whose advice did not apply).
        string source = "#if SM6\n#define PS_SHADERMODEL ps_6_0\n#else\n#define PS_SHADERMODEL ps_5_0\n#endif\n" +
                        "Texture2D T; SamplerState S;\n" +
                        declaration + "\n" +
                        $"float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0 {{ return T.Sample(S, uv) * {read}; }}\n" +
                        "technique T0 { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }\n";

        var result = await CompileSourceAsync(source, "Buffers.fx");

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0233");
        error.Message.ShouldContain(expected, Case.Sensitive);
        error.Line.ShouldBe(7);
        error.Column.ShouldBe(declaration.IndexOf(" Buf", StringComparison.Ordinal) + 2);
    }

    // ---- A helper called only through a macro (the fourth review's BLOCKER) --------------------

    // The pairing read each function's calls off the raw text, so a helper called only out of a
    // #define looked uncalled: its Sample paired nothing, the sampler counted as dead and was moved
    // off its texture's register, and DXC split the pair. In a file that also tests a macro only DXC
    // defines the preprocessed retry that rescues this is unavailable, so it failed as SD0232 where
    // 0.22.1 compiled one combined descriptor at slot 0. The calls are now read from the managed
    // preprocessor's macro-expanded view, and a file whose view is untrusted pairs over the whole file.
    public static TheoryData<string, string> MacroCalledHelpers()
    {
        const string modern = "Texture2D Tex : register(t0);\nSamplerState Smp : register(s0);\n";
        const string unregistered = "Texture2D Tex;\nSamplerState Smp;\n";
        const string legacy = "texture Tex;\nsampler Smp = sampler_state { Texture = <Tex>; };\n";
        const string helper = "float4 Helper(float2 uv) { return Tex.Sample(Smp, uv); }\n";
        const string legacyHelper = "float4 Helper(float2 uv) { return tex2D(Smp, uv); }\n";
        var data = new TheoryData<string, string>();
        foreach (string conditional in new[] { "", "#ifdef __spirv__\n#endif\n", "#if defined(__SHADER_TARGET_MAJOR)\n#endif\n", "#if __HLSL_VERSION\n#endif\n" })
        {
            string tag = conditional.Length == 0 ? "no DXC macro" : conditional.Split('\n')[0];
            data.Add($"function-like macro, registers, {tag}", modern + helper + "#define DO_SAMPLE(uv) Helper(uv)\n" + conditional + "%BODY%DO_SAMPLE(uv)");
            data.Add($"function-like macro, no registers, {tag}", unregistered + helper + "#define DO_SAMPLE(uv) Helper(uv)\n" + conditional + "%BODY%DO_SAMPLE(uv)");
            data.Add($"object-like macro, {tag}", modern + helper + "#define FN Helper\n" + conditional + "%BODY%FN(uv)");
            data.Add($"legacy sampler_state, {tag}", legacy + legacyHelper + "#define DO_SAMPLE(uv) Helper(uv)\n" + conditional + "%BODY%DO_SAMPLE(uv)");
        }
        return data;
    }

    private const string MacroProbePs =
        "float4 PS(float4 p : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return %CALL%; }\n" +
        "technique T { pass P { PixelShader = compile ps_4_0 PS(); } }\n";

    private static string MacroProbe(string shape)
    {
        int at = shape.IndexOf("%BODY%", StringComparison.Ordinal);
        return shape[..at] + MacroProbePs.Replace("%CALL%", shape[(at + "%BODY%".Length)..], StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MacroCalledHelpers))]
    public async Task Vulkan_AHelperCalledThroughAMacro_IsOneCombinedDescriptorAtSlot0_AsIn0221(string name, string shape)
    {
        var result = await CompileSourceAsync(MacroProbe(shape), "MacroHelper.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{name}: " + string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        var subject = MgfxBlobReader.Parse(result.Value.Data);
        MgfxSamplerRecord record = subject.Samplers.ShouldHaveSingleItem(name);
        (subject.ParameterNames[record.Parameter], record.TextureSlot, record.SamplerSlot).ShouldBe(("Tex", (byte)0, (byte)0), name);
        VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode).Bindings
            .Select(b => (b.Binding, b.DescriptorType)).ShouldBe([(32u, CombinedImageSampler)], name);
        result.Value.Warnings.ShouldNotContain(w => w.Code == "SD0235", name);
    }

    [Theory]
    [InlineData("macro", "")]
    [InlineData("macro", "#ifdef __spirv__\n#endif\n")]
    [InlineData("direct", "")]
    [InlineData("direct", "#ifdef __spirv__\n#endif\n")]
    public async Task Vulkan_ASamplerSharedWithAHelperCalledThroughAMacro_IsSD0028_LikeTheDirectForm(string form, string conditional)
    {
        // Through the macro it compiled to A + S at binding 32 and B as a lone SAMPLED_IMAGE at 33
        // (sampler slot 255) while the shader samples B through S: a silent wrong render. 0.22.1 and the
        // direct form both refuse it.
        string source =
            "Texture2D A; Texture2D B; SamplerState S;\n" +
            "float4 HB(float2 uv) { return B.Sample(S, uv); }\n" +
            "#define DOB(uv) HB(uv)\n" +
            conditional +
            MacroProbePs.Replace("%CALL%", "A.Sample(S, uv) + " + (form == "macro" ? "DOB(uv)" : "HB(uv)"), StringComparison.Ordinal);

        var result = await CompileSourceAsync(source, "SharedMacro.fx");

        result.IsFailure.ShouldBeTrue($"{form}, '{conditional}': compiled where it must be SD0028");
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0028");
        // HB's call is first in the file, so B is named first.
        error.Message.ShouldContain("textures 'B' and 'A' are both sampled through the single sampler 'S'", Case.Sensitive);
    }

    [Fact]
    public async Task Vulkan_AFileTestingADxcOnlyMacro_PairsOverTheWholeFile_AsBefore()
    {
        // The managed view cannot know __HLSL_VERSION (SD0009), so it neither drops the '#if 0' line
        // nor reads the calls: every line and every function counts, as in the released rewrite. The
        // dead call comes first and decides SpriteTexture's sampler, so the live one gets an alias
        // (SD0235); DXC then removes the original and the alias keeps its parameter name.
        const string source = """
            #if __HLSL_VERSION >= 2016
            #endif
            Texture2D SpriteTexture; SamplerState U; SamplerState S;
            float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0 {
            #if 0
              return SpriteTexture.Sample(U, uv);
            #endif
              return SpriteTexture.Sample(S, uv) * color;
            }
            technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }
            """;

        var result = await CompileSourceAsync(source, "DxcMacro.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        result.Value.Warnings.ShouldContain(w => w.Code == "SD0009" && w.Message.Contains("__HLSL_VERSION", StringComparison.Ordinal));
        result.Value.Warnings.ShouldContain(w => w.Code == "SD0235");
        var subject = MgfxBlobReader.Parse(result.Value.Data);
        MgfxSamplerRecord record = subject.Samplers.ShouldHaveSingleItem();
        subject.ParameterNames[record.Parameter].ShouldBe("SpriteTexture");
        subject.ParameterNames.ShouldNotContain(n => n.Contains("_SDVkAlias", StringComparison.Ordinal));
        VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode).Bindings
            .Select(b => b.DescriptorType).ShouldBe([CombinedImageSampler]);
    }

    // ---- Qualified calls and anonymous structs reach the helper (review of cb0fda33) ----------------

    // A helper that makes one Sample call (%SAMPLE%), defined so that only a qualified call or a member call on an
    // anonymous struct reaches it, and how the pixel shader calls it (%LOCAL%: `H h; ... h.Get(uv)`).
    public static TheoryData<string, string, string> QualifiedHelpers() => new()
    {
        { "namespace", "namespace Lib { float4 H(float2 uv) { return %SAMPLE%; } }", "Lib::H(uv)" },
        { "anonymous struct", "typedef struct { float4 Get(float2 uv) { return %SAMPLE%; } } H;", "%LOCAL%" },
        { "static method", "struct H { static float4 Get(float2 uv) { return %SAMPLE%; } };", "H::Get(uv)" },
    };

    private static string QualifiedProbe(string helper, string call, string sample, string declarations, string extra = "")
    {
        string body = call == "%LOCAL%"
            ? "{ H h; return " + extra + "h.Get(uv); }"
            : "{ return " + extra + call + "; }";
        return declarations + "\n" +
               helper.Replace("%SAMPLE%", sample, StringComparison.Ordinal) + "\n" +
               "float4 PS(float4 p : SV_Position, float2 uv : TEXCOORD0) : SV_Target " + body + "\n" +
               "technique T { pass P { PixelShader = compile ps_4_0 PS(); } }\n";
    }

    [Theory]
    [MemberData(nameof(QualifiedHelpers))]
    public async Task Vulkan_ASecondTextureOnASharedSamplerInAQualifiedOrAnonymousStructHelper_IsSD0028(string name, string helper, string call)
    {
        // `Lib::H(uv)` was read as a member call and reached only struct methods, and an anonymous
        // struct's method counted as a free function a member call never reaches: either way the helper
        // was dead, B's pairing was dropped, and the shader compiled A + S at binding 32 with B a lone
        // SAMPLED_IMAGE at 33 (sampler slot 255) while it samples B through S, a silent wrong render.
        // 0.22.1 refuses it.
        string source = QualifiedProbe(helper, call, "B.Sample(S, uv)", "Texture2D A; Texture2D B; SamplerState S;", extra: "A.Sample(S, uv) + ");

        var result = await CompileSourceAsync(source, "Qualified.fx");

        result.IsFailure.ShouldBeTrue($"{name}: compiled where it must be SD0028");
        result.Error.ShouldHaveSingleItem().Code.ShouldBe("SD0028", name);
    }

    [Theory]
    [MemberData(nameof(QualifiedHelpers))]
    public async Task Vulkan_APairInAQualifiedOrAnonymousStructHelper_IsOneCombinedDescriptorAtSlot0(string name, string helper, string call)
    {
        // The helper's Sample is the pairing (0.22.1 compiled it to one descriptor at slot 0; the
        // unreached helper used to leave Smp a standalone sampler, SD0232). A dead free function samples
        // Tex through U first in the file: reached by name it would alias Tex.
        foreach (string registers in new[] { "", " : register(t0)|: register(s0)" })
        {
            string[] r = registers.Length == 0 ? ["", ""] : registers.Split('|');
            string declarations = $"Texture2D Tex{r[0]}; SamplerState Smp{r[1]}; SamplerState U;\n" +
                                  "float4 Dead(float2 uv) { return Tex.Sample(U, uv); }";
            string source = QualifiedProbe(helper, call, "Tex.Sample(Smp, uv)", declarations);

            var result = await CompileSourceAsync(source, "QualifiedPair.fx");

            result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{name} '{registers}': " + string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
            var subject = MgfxBlobReader.Parse(result.Value.Data);
            MgfxSamplerRecord record = subject.Samplers.ShouldHaveSingleItem(name);
            (subject.ParameterNames[record.Parameter], record.TextureSlot, record.SamplerSlot).ShouldBe(("Tex", (byte)0, (byte)0), name);
            VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode).Bindings
                .Select(b => (b.Binding, b.DescriptorType)).ShouldBe([(32u, CombinedImageSampler)], name);
            result.Value.Warnings.ShouldNotContain(w => w.Code == "SD0235", name);
        }
    }

    [Fact]
    public async Task Vulkan_ATextureReadThroughASecondSamplerInANamespaceHelper_GetsItsAlias()
    {
        // A direct call through A and `Lib::H` through B: two combined descriptors bound to Tex (the
        // unreached helper used to leave B a standalone sampler, SD0232).
        const string source = """
            Texture2D Tex; SamplerState A; SamplerState B;
            namespace Lib { float4 H(float2 uv) { return Tex.Sample(B, uv); } }
            float4 PS(float4 p : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return Tex.Sample(A, uv) + Lib::H(uv); }
            technique T { pass P { PixelShader = compile ps_4_0 PS(); } }
            """;

        var result = await CompileSourceAsync(source, "NamespaceAlias.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        var subject = MgfxBlobReader.Parse(result.Value.Data);
        subject.Samplers.Select(s => (subject.ParameterNames[s.Parameter], s.TextureSlot, s.SamplerSlot))
            .ShouldBe([("Tex", (byte)0, (byte)0), ("Tex", (byte)1, (byte)1)]);
        VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode).Bindings
            .Select(b => (b.Binding, b.DescriptorType)).ShouldBe([(32u, CombinedImageSampler), (33u, CombinedImageSampler)]);
    }

    // ---- An alias stays inside the stage's texture slots -----------------------------------------------

    [Theory]
    [InlineData("register(t15)", 2, new byte[] { 1, 15 })]
    [InlineData("register(t14)", 3, new byte[] { 1, 14, 15 })]
    public async Task Vulkan_AnAliasOfATextureNearTheLastSlot_FallsBackBelowIt_NotPastSlot15(string register, int samplers, byte[] slots)
    {
        // MonoGame's DesktopVK has 16 pixel texture slots; an alias used to land on slot 16 (binding 48),
        // past every texture collection. It takes the lowest free slot below its texture instead, never 0.
        string[] names = ["A", "B", "C"];
        string source =
            $"Texture2D Tex : {register}; " + string.Join(" ", names.Take(samplers).Select(n => $"SamplerState {n};")) + "\n" +
            "float4 PS(float4 p : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return " +
            string.Join(" + ", names.Take(samplers).Select(n => $"Tex.Sample({n}, uv)")) + "; }\n" +
            "technique T { pass P { PixelShader = compile ps_4_0 PS(); } }\n";

        var result = await CompileSourceAsync(source, "AliasNearLastSlot.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        var subject = MgfxBlobReader.Parse(result.Value.Data);
        subject.Samplers.Select(s => s.TextureSlot).OrderBy(s => s).ShouldBe(slots);
        subject.Samplers.ShouldAllBe(s => subject.ParameterNames[s.Parameter] == "Tex");
        VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode).Bindings
            .Select(b => (b.Binding, b.DescriptorType)).OrderBy(b => b.Binding)
            .ShouldBe(slots.Select(s => (32u + s, CombinedImageSampler)));
    }

    // ---- A #define body counts only where its expansion is reachable ----------------------------------

    [Fact]
    public async Task Vulkan_AMacroOnlyAnotherTechniqueUses_DoesNotMoveThisShadersSpriteTexture()
    {
        // SAMPLE_POINT is used only by PS_Point. Its body used to count in PS_Linear too, so PS_Linear's
        // SpriteTexture went to slot 1 behind an alias (SD0235) and SpriteBatch, which fills slot 0, drew
        // nothing. mgfxc puts it on slot 0 in both shaders.
        const string source = """
            Texture2D SpriteTexture; SamplerState PointSampler; SamplerState SpriteSampler;
            #define SAMPLE_POINT(uv) SpriteTexture.Sample(PointSampler, uv)
            float4 PS_Linear(float4 p : SV_Position, float4 c : COLOR0, float2 uv : TEXCOORD0) : SV_Target { return SpriteTexture.Sample(SpriteSampler, uv) * c; }
            float4 PS_Point(float4 p : SV_Position, float4 c : COLOR0, float2 uv : TEXCOORD0) : SV_Target { return SAMPLE_POINT(uv) * c; }
            technique Linear { pass P { PixelShader = compile ps_4_0 PS_Linear(); } }
            technique Point { pass P { PixelShader = compile ps_4_0 PS_Point(); } }
            """;

        var result = await CompileSourceAsync(source, "DefineTwoTechniques.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        result.Value.Warnings.ShouldNotContain(w => w.Code == "SD0235");
        var subject = MgfxBlobReader.Parse(result.Value.Data);
        subject.Samplers.Count.ShouldBe(2);
        subject.Samplers.ShouldAllBe(s => subject.ParameterNames[s.Parameter] == "SpriteTexture" && s.TextureSlot == 0 && s.SamplerSlot == 0);
        foreach (var shader in subject.Shaders)
        {
            VulkanShaderCodeReader.Parse(shader.Bytecode).Bindings
                .Select(b => (b.Binding, b.DescriptorType)).ShouldBe([(32u, CombinedImageSampler)]);
        }
    }

    [Fact]
    public async Task Vulkan_AMacroOnlyTheVertexShaderReaches_DoesNotAliasThePixelShadersTexture()
    {
        // VSAMP's Tex-through-B is in HV, which only the vertex shader calls (through another macro). The
        // pixel shader samples Tex through A alone: one descriptor at slot 0 in each stage, no alias.
        const string source = """
            Texture2D Tex; SamplerState A; SamplerState B;
            #define VSAMP(uv) Tex.SampleLevel(B, uv, 0)
            float4 HV(float2 uv) { return VSAMP(uv); }
            float4 HP(float2 uv) { return Tex.Sample(A, uv); }
            #define CALLV HV
            void VS(float4 p : POSITION0, float2 uv : TEXCOORD0, out float4 op : SV_Position, out float4 c : COLOR0) { op = p; c = CALLV(uv); }
            float4 PS(float4 p : SV_Position, float4 c : COLOR0, float2 uv : TEXCOORD0) : SV_Target { return HP(uv) * c; }
            technique T { pass P { VertexShader = compile vs_4_0 VS(); PixelShader = compile ps_4_0 PS(); } }
            """;

        var result = await CompileSourceAsync(source, "DefineVertexOnly.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        result.Value.Warnings.ShouldNotContain(w => w.Code == "SD0235");
        var subject = MgfxBlobReader.Parse(result.Value.Data);
        subject.Samplers.Count.ShouldBe(2);
        subject.Samplers.ShouldAllBe(s => subject.ParameterNames[s.Parameter] == "Tex" && s.TextureSlot == 0 && s.SamplerSlot == 0);
        foreach (var shader in subject.Shaders)
        {
            VulkanShaderCodeReader.Parse(shader.Bytecode).Bindings
                .Select(b => (b.Binding, b.DescriptorType)).ShouldBe([(32u, CombinedImageSampler)]);
        }
    }

    [Fact]
    public async Task Vulkan_ASharedSamplerOnlyAnUnusedMacroPairs_IsNotSD0028()
    {
        // SAMPLE_B is never used, so B is not sampled through S: one pair, A + S at slot 0.
        const string source = """
            Texture2D A; Texture2D B; SamplerState S;
            #define SAMPLE_B(uv) B.Sample(S, uv)
            float4 PS(float4 p : SV_Position, float2 uv : TEXCOORD0) : SV_Target { return A.Sample(S, uv); }
            technique T { pass P { PixelShader = compile ps_4_0 PS(); } }
            """;

        var result = await CompileSourceAsync(source, "DefineUnused.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        var subject = MgfxBlobReader.Parse(result.Value.Data);
        MgfxSamplerRecord record = subject.Samplers.ShouldHaveSingleItem();
        (subject.ParameterNames[record.Parameter], record.TextureSlot, record.SamplerSlot).ShouldBe(("A", (byte)0, (byte)0));
        VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode).Bindings
            .Select(b => (b.Binding, b.DescriptorType)).ShouldBe([(32u, CombinedImageSampler)]);
    }

    // ---- A conditional on a macro DXC predefines ------------------------------------------------------

    [Theory]
    [InlineData("__GNUC__")]
    [InlineData("__GNUC_MINOR__")]
    [InlineData("__GNUC_PATCHLEVEL__")]
    [InlineData("__GXX_ABI_VERSION")]
    [InlineData("__llvm__")]
    [InlineData("__BYTE_ORDER__")]
    [InlineData("__ORDER_LITTLE_ENDIAN__")]
    [InlineData("__LITTLE_ENDIAN__")]
    [InlineData("__FLT_MAX__")]
    [InlineData("__DBL_MAX__")]
    public async Task Vulkan_APairingUnderIfdefOfAMacroDxcPredefines_IsTheOneDxcCompiles(string macro)
    {
        // DXC predefines each of these (measured with `dxc -P`), so it compiles the first branch. When
        // the managed preprocessor did not know that, its view dropped that branch as skipped and the
        // pairing read the #else branch's texture: TexA was bound at slot 0 while DXC sampled TexB, a
        // SILENT wrong render. FxMacroPreprocessor.IsCompilerPredefined now lists the macro, so the whole
        // file is paired and the texture DXC samples is the one bound.
        string source = $$"""
            Texture2D TexA; Texture2D TexB; SamplerState S;
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0 {
            #ifdef {{macro}}
              return TexB.Sample(S, uv);
            #else
              return TexA.Sample(S, uv);
            #endif
            }
            technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }
            """;

        var result = await CompileSourceAsync(source, "Predefined.fx");

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        var subject = MgfxBlobReader.Parse(result.Value.Data);
        MgfxSamplerRecord record = subject.Samplers.ShouldHaveSingleItem();
        (subject.ParameterNames[record.Parameter], record.TextureSlot, record.SamplerSlot).ShouldBe(("TexB", (byte)0, (byte)0));
        VulkanShaderCodeReader.Parse(subject.Shaders.Single().Bytecode).Bindings
            .Select(b => (b.Binding, b.DescriptorType)).ShouldBe([(32u, CombinedImageSampler)]);
    }

    private static async Task<Result<CompiledShader, ShaderError[]>> CompileSourceAsync(string source, string fileName)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        return await new EffectCompiler().CompileAsync(source, new CompilerOptions { Target = PlatformTarget.Vulkan, SourceFileName = fileName }, cts.Token);
    }

    // ---- Corpus-wide: a texture parameter's Vulkan slot is its DirectX 12 slot ------------------

    public static TheoryData<string> CorpusFixtures()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders");
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*.fx", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFixtures))]
    public async Task EveryFixtureThatCompilesForBoth_HasTheSameTextureSlotsOnVulkanAsOnDirectX12(string relativePath)
    {
        string fxPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders", relativePath);
        string source = await File.ReadAllTextAsync(fxPath);
        using var cts = new CancellationTokenSource(TestBudget.Compile);

        async Task<MgfxBlobReader?> Compile(PlatformTarget target)
        {
            var r = await new EffectCompiler().CompileAsync(source, new CompilerOptions
            {
                Target = target,
                IncludeResolver = new FileSystemIncludeResolver(),
                SourceFileName = fxPath,
            }, cts.Token);
            return r.IsSuccess ? MgfxBlobReader.Parse(r.Value.Data) : null;
        }

        MgfxBlobReader? vulkan = await Compile(PlatformTarget.Vulkan);
        MgfxBlobReader? dx12 = vulkan is null ? null : await Compile(PlatformTarget.DirectX12);
        if (vulkan is null || dx12 is null)
            return; // a refusal on either target is pinned by the byte-identity manifests

        // Every DirectX 12 (parameter, slot) is on Vulkan too. Vulkan may bind a parameter at one more
        // slot than DirectX 12: a texture sampled through two samplers needs a second combined
        // descriptor (an alias of the texture), whose record binds the same parameter.
        var vulkanSlots = TextureSlots(vulkan);
        var dx12Slots = TextureSlots(dx12);
        dx12Slots.ShouldBeSubsetOf(vulkanSlots, $"{relativePath}: every DirectX 12 texture parameter -> slot must be the Vulkan one too");
        vulkanSlots.Select(s => (s.IsVertex, s.Parameter)).Distinct()
            .ShouldBe(dx12Slots.Select(s => (s.IsVertex, s.Parameter)).Distinct(), ignoreOrder: true,
                      customMessage: $"{relativePath}: Vulkan binds the same texture parameters as DirectX 12");
        // An extra Vulkan slot is only ever an alias: the parameter's own slot is still its DirectX one.
        foreach (var group in vulkanSlots.GroupBy(s => (s.IsVertex, s.Parameter)))
            group.Min(s => s.Slot).ShouldBe(dx12Slots.Where(s => (s.IsVertex, s.Parameter) == group.Key).Min(s => s.Slot),
                $"{relativePath}: {group.Key.Parameter}'s first Vulkan slot is its DirectX 12 slot");
    }

    // (shader stage, parameter name) -> texture slot, over every record of every shader.
    private static List<(bool IsVertex, string Parameter, byte Slot)> TextureSlots(MgfxBlobReader reader) =>
        reader.Samplers
            .Select(s => (reader.Shaders.Single(sh => sh.Index == s.ShaderIndex).IsVertex, reader.ParameterNames[s.Parameter], s.TextureSlot))
            .Distinct()
            .OrderBy(x => x.IsVertex).ThenBy(x => x.Item2, StringComparer.Ordinal).ThenBy(x => x.TextureSlot)
            .ToList();

    private static async Task<byte[]> CompileAsync(string stem, PlatformTarget target) =>
        (await CompileShaderAsync(stem, target)).Data;

    private static async Task<CompiledShader> CompileShaderAsync(string stem, PlatformTarget target)
    {
        string fxPath = stem.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? TestHelpers.FixturePath(stem + ".fx")
            : TestHelpers.FixturePath(Path.Combine("register-slots", stem + ".fx"));
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        var result = await new EffectCompiler().CompileAsync(await File.ReadAllTextAsync(fxPath, cts.Token), new CompilerOptions
        {
            Target = target,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName = fxPath,
        }, cts.Token);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        return result.Value;
    }

    private static string GoldenPath(string stem)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
            dir = dir.Parent;
        dir.ShouldNotBeNull("repo root (ShadowDusk.slnx)");
        string path = Path.Combine(dir.FullName, "tests", "fixtures", "golden", "Vulkan", stem + ".mgfx");
        File.Exists(path).ShouldBeTrue($"mgfxc 3.8.5 /Profile:Vulkan golden must exist at {path}");
        return path;
    }
}
