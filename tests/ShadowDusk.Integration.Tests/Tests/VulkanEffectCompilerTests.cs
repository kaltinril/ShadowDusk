#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Phase 32 v2 — the empirical proof/disproof of the reflection fix and the real
/// Vulkan container: compiles a shader with a constant buffer AND a texture/sampler
/// pair, then asserts everything <c>Compile_Minimal_Vulkan_ReturnsBytes</c> can't
/// (that fixture has no parameters to lose): the real profile byte (80, not the
/// old wrong placeholder 3), the SPIR-V magic word inside the wrapped
/// <c>ShaderCode</c> field, and non-empty, structurally correct reflection.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "Vulkan")]
public sealed class VulkanEffectCompilerTests
{
    // Modern SM6-safe syntax throughout (Texture2D/SamplerState/.Sample()/SV_Target) —
    // legacy sampler2D/tex2D/COLOR is rejected by DXC at Vulkan's forced vs_6_0/ps_6_0.
    private const string ParameterizedShader = """
        float4 Tint;
        Texture2D SpriteTexture;
        SamplerState SpriteTextureSampler;

        struct VOut { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

        VOut VS(float4 pos : POSITION0, float2 uv : TEXCOORD0)
        {
            VOut o;
            o.Pos = pos;
            o.UV = uv;
            return o;
        }

        float4 PS(VOut i) : SV_Target0
        {
            return SpriteTexture.Sample(SpriteTextureSampler, i.UV) * Tint;
        }

        technique T
        {
            pass P { VertexShader = compile vs_6_0 VS(); PixelShader = compile ps_6_0 PS(); }
        }
        """;

    [Fact]
    public async Task Compile_ParameterizedShader_ProducesRealVulkanContainer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await new EffectCompiler().CompileAsync(ParameterizedShader, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "VulkanParameterized.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        reader.ProfileId.ShouldBe((byte)(80), customMessage: "MgfxProfile.Vulkan is 80, matching real MonoGame 3.8.5 (not the old wrong placeholder 3)");
        reader.MgfxVersion.ShouldBe((byte)(11), customMessage: "Vulkan always writes the v11 shader-record shape");

        reader.Shaders.Count().ShouldBe(2, customMessage: "one vertex + one pixel shader");
        foreach (var shader in reader.Shaders)
        {
            var vk = VulkanShaderCodeReader.Parse(shader.Bytecode);
            vk.SpirvMagicOk.ShouldBeTrue($"shader #{shader.Index} (isVertex={shader.IsVertex}) must wrap valid SPIR-V");
        }

        // The pixel shader reflects both the texture/sampler AND the Tint parameter
        // (packed into $Globals) — this is exactly what the reflection-gate fix
        // (CompilationPipeline.cs) makes possible; before it, every Vulkan shader's
        // reflection was silently empty regardless of what the HLSL declared.
        reader.ConstantBuffers.ShouldNotBeEmpty("Tint must be reflected into a constant buffer");
        reader.Samplers.ShouldNotBeEmpty("SpriteTexture/SpriteTextureSampler must be reflected");
        reader.ParameterNames.ShouldContain("Tint");

        var pixelShader = reader.Shaders.Single(s => !s.IsVertex);
        pixelShader.ConstantBufferIndices.ShouldNotBeEmpty("the pixel shader stage must bind its constant buffer");
    }

    [Fact]
    public async Task Compile_Sepia_ImplicitGlobalsCbufferBindsToZero()
    {
        // Regression test for a real native crash (2026-07-18), reproduced via the
        // ACTUAL corpus fixture (not a hand-written analog — a synthetic shader with the
        // same "texture/sampler before loose global" shape did NOT reproduce this; DXC's
        // auto-numbering is sensitive to the full real preprocessed source, not just
        // declaration order). Sepia.fx's implicit "$Globals" cbuffer (holding _sepiaTone)
        // auto-numbered to raw SPIR-V binding 1, not 0, and MonoGame's native DesktopVK
        // draw path crashed (AccessViolationException) the instant that binding wasn't 0.
        // Isolated by rendering Sepia.fx alone: forcing an explicit register(b0) fixed
        // it, pointing at DXC's -fvk-bind-globals flag as the real fix (DxcFlagBuilder.cs)
        // rather than a source-level workaround.
        var result = await TestHelpers.CompileFixtureAsync("Sepia.fx", "Vulkan");

        result.ExitCode.ShouldBe(0, customMessage: result.Stderr);

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        var pixelShader = reader.Shaders.Single(s => !s.IsVertex);
        var vk = VulkanShaderCodeReader.Parse(pixelShader.Bytecode);

        vk.Bindings.ShouldContain(
            b => b.DescriptorType == 8 && b.Binding == 0, "the implicit $Globals cbuffer (VkDescriptorType UNIFORM_BUFFER_DYNAMIC=8) must always bind to 0, " +
            "regardless of where DXC would otherwise auto-number it");
    }

    [Fact]
    public async Task Compile_TwoConstantBuffers_FailsLoudly()
    {
        // Real mgfxc's own Vulkan writer throws on a second cbuffer per shader stage
        // (VulkanShaderProfile.CreateShader) — ShadowDusk must fail loudly too rather
        // than silently drop or mis-bind one the container can't represent.
        const string twoCbuffers = """
            cbuffer A { float4 A0; };
            cbuffer B { float4 B0; };
            struct VOut { float4 Pos : SV_POSITION; };
            VOut VS(float4 pos : POSITION0) { VOut o; o.Pos = pos; return o; }
            float4 PS(VOut i) : SV_Target0 { return A0 + B0; }
            technique T { pass P { VertexShader = compile vs_6_0 VS(); PixelShader = compile ps_6_0 PS(); } }
            """;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await new EffectCompiler().CompileAsync(twoCbuffers, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "VulkanTwoCbuffers.fx",
        }, cts.Token);

        result.IsFailure.ShouldBeTrue("Vulkan does not support more than one constant buffer per shader stage");
        result.Error.Select(e => e.Code).ShouldContain("SD0026");
    }

    // ── Issue #145 regressions ────────────────────────────────────────────────
    //
    // apos-shapes.fx on real MonoGame 3.8.5 DesktopVK: the SM6 branch loaded and drew but
    // rendered nothing, and the legacy tex2D branch access-violated inside native
    // GraphicsDevice_DrawIndexed. Both were ShadowDusk bugs, both invisible to a PS-only,
    // matrix-free, modern-syntax-only Vulkan corpus. Each test below fails on the pre-fix
    // compiler.

    private const string MatrixVertexShader = """
        float4x4 view_projection;

        struct VOut { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

        VOut VS(float4 pos : POSITION0, float2 uv : TEXCOORD0)
        {
            VOut o;
            o.Pos = mul(pos, view_projection);
            o.UV = uv;
            return o;
        }

        float4 PS(VOut i) : SV_Target0 { return float4(i.UV, 0, 1); }

        technique T
        {
            pass P { VertexShader = compile vs_6_0 VS(); PixelShader = compile ps_6_0 PS(); }
        }
        """;

    [Fact]
    public async Task Compile_Vulkan_PacksMatricesColumnMajorLikeMgfxc()
    {
        // BUG 1. -Zpr (row-major) was applied to every DXC compile, Vulkan included, but
        // MonoGame uploads a Matrix parameter for HLSL's COLUMN-major default
        // (EffectParameter.SetValue(Matrix) transposes on assignment; ConstantBuffer
        // .SetParameter's own comment says "HLSL assumes matrices are column-major … TODO:
        // HLSL can be told to use row-major. We should handle that too."). So every matrix
        // arrived transposed and a VS-driven effect threw its geometry out of clip space.
        // mgfxc's Vulkan command line carries no -Zpr; the shipped SPIR-V must agree.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await new EffectCompiler().CompileAsync(MatrixVertexShader, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "VulkanMatrixVs.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var vertexShader = reader.Shaders.Single(s => s.IsVertex);
        byte[] spirv = VulkanShaderCodeReader.Parse(vertexShader.Bytecode).Spirv;

        SpirvDecorationScanner.AllMatrixMembersAreSpirvRowMajor(spirv).ShouldBeTrue(
            "DXC emits the SPIR-V RowMajor decoration for an HLSL COLUMN-major matrix, which is " +
            "what mgfxc ships and what MonoGame's runtime uploads for; a ColMajor decoration " +
            "means -Zpr leaked back in and every matrix will be read transposed");
    }

    [Fact]
    public async Task Compile_Vulkan_ShipsNoGoogleReflectionExtensions()
    {
        // DIVERGENCE S3. mgfxc compiles twice so the SHIPPED module has no -fspv-reflect
        // Google extensions; ShadowDusk reflects from core decorations only, so it simply
        // never requests them.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await new EffectCompiler().CompileAsync(MatrixVertexShader, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "VulkanNoGoogleExt.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue();

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        foreach (var shader in reader.Shaders)
        {
            byte[] spirv = VulkanShaderCodeReader.Parse(shader.Bytecode).Spirv;

            SpirvDecorationScanner.Extensions(spirv).ShouldNotContain(
                e => e.StartsWith("SPV_GOOGLE", StringComparison.Ordinal), "the shipped module must match mgfxc's reflect-free second compile");
            SpirvDecorationScanner.EntryPointName(spirv).ShouldBe("main", customMessage: "MonoGame's native Vulkan pipeline creation expects the entry point to be named main");
        }
    }

    [Fact]
    public async Task Compile_Vulkan_VertexShader_EmitsTheAttributeTable()
    {
        // DIVERGENCE S1. mgfxc emits one attribute entry per VS input location (usage +
        // semantic index); ShadowDusk emitted an empty table.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await new EffectCompiler().CompileAsync(MatrixVertexShader, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "VulkanAttributes.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue();

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var vertexShader = reader.Shaders.Single(s => s.IsVertex);

        // POSITION0 -> usage 0 index 0, TEXCOORD0 -> usage 2 index 0, ordered by location.
        vertexShader.Attributes.Count().ShouldBe(2);
        vertexShader.Attributes[0].Usage.ShouldBe((byte)(0));
        vertexShader.Attributes[0].Index.ShouldBe((byte)(0));
        vertexShader.Attributes[1].Usage.ShouldBe((byte)(2));
        vertexShader.Attributes[1].Index.ShouldBe((byte)(0));

        reader.Shaders.Single(s => !s.IsVertex).Attributes.ShouldBeEmpty(
            "only vertex shaders carry an attribute table");
    }

    [Fact]
    public async Task Compile_Vulkan_LegacyTex2DSource_EmitsOnlyCombinedImageSamplers()
    {
        // BUG 2 — the native access violation. FxPreParser converts legacy sampler/tex2D
        // source to modern syntax, synthesizing "<sampler>_SDTexture" textures. Those were
        // excluded from the register-pairing rewrite, so the image auto-numbered to raw
        // binding 0/1 while its sampler shifted to 32/33 — two separate descriptors, and
        // MonoGame's MGVK_UpdateDescriptors turns a binding-0 image into
        // device->textures[stage][0 - 32] (plus an unhandled VK_DESCRIPTOR_TYPE_SAMPLER
        // branch). Every legacy-syntax shader crashed on Vulkan.
        const string legacy = """
            float4x4 view_projection;
            sampler TextureSampler : register(s0);
            sampler FontSampler;

            struct VOut { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

            VOut VS(float4 pos : POSITION0, float2 uv : TEXCOORD0)
            {
                VOut o;
                o.Pos = mul(pos, view_projection);
                o.UV = uv;
                return o;
            }

            float4 PS(VOut i) : SV_Target0
            {
                return tex2D(TextureSampler, i.UV) + tex2D(FontSampler, i.UV);
            }

            technique T
            {
                pass P { VertexShader = compile vs_6_0 VS(); PixelShader = compile ps_6_0 PS(); }
            }
            """;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await new EffectCompiler().CompileAsync(legacy, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "VulkanLegacySamplers.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var vk = VulkanShaderCodeReader.Parse(reader.Shaders.Single(s => !s.IsVertex).Bytecode);

        vk.Bindings.Count().ShouldBe(2, customMessage: "two texture/sampler pairs, one combined descriptor each");

        foreach (var binding in vk.Bindings)
        {
            binding.DescriptorType.ShouldBe((uint)(1), customMessage: "VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER — a separate SAMPLED_IMAGE/SAMPLER pair " +
                "hits an unhandled branch in MonoGame's native descriptor writer");
            binding.Binding.ShouldBeGreaterThanOrEqualTo(32u, customMessage: "the runtime recovers the texture slot as (binding - 32); anything below 32 indexes " +
                "the texture array out of bounds");
        }

        vk.Bindings.Select(b => b.Binding).ShouldBeUnique(
            "two descriptor-set-layout bindings at the same binding number is invalid");
        vk.SamplerSlots.ShouldBe(vk.TextureSlots, customMessage: "a combined descriptor occupies both the texture and sampler slot masks, as mgfxc writes them");
    }

    [Fact]
    public async Task Compile_UnrecognizedVertexSemantic_WarnsSd0104_AndStillCompiles()
    {
        // Bug-hunt 2026-07-27 N5, warning half — the Vulkan (SPIR-V) half of the same
        // plumbing the DirectX12 test pins. TEXCORD0 is the classic typo for TEXCOORD0:
        // mgfxc accepts it and defaults the element to TextureCoordinate WITH a warning,
        // so a drop-in replacement must keep the default AND print the warning. Without
        // it, the typo silently mints a phantom TextureCoordinate attribute the consumer's
        // vertex declaration has to supply.
        const string typo = """
            struct VOut { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

            VOut VS(float4 pos : POSITION0, float2 uv : TEXCORD0)
            {
                VOut o;
                o.Pos = pos;
                o.UV = uv;
                return o;
            }

            float4 PS(VOut i) : SV_Target0 { return float4(i.UV, 0, 1); }

            technique T
            {
                pass P { VertexShader = compile vs_6_0 VS(); PixelShader = compile ps_6_0 PS(); }
            }
            """;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await new EffectCompiler().CompileAsync(typo, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "VulkanTypoSemantic.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var warning = result.Value.Warnings.SingleOrDefault(w => w.Code == "SD0104");
        warning.ShouldNotBeNull("the unrecognized semantic must surface as SD0104, as mgfxc warns");
        warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warning.Message.ShouldContain("TEXCORD0", Case.Sensitive);
        warning.File.ShouldBe("VulkanTypoSemantic.fx");

        // Unchanged output: the fallback attribute is still written.
        var reader = MgfxBlobReader.Parse(result.Value.Data);
        var vertexShader = reader.Shaders.Single(s => s.IsVertex);
        vertexShader.Attributes.Select(a => (a.Usage, a.Index))
            .ShouldBe(new[] { ((byte)0, (byte)0), ((byte)2, (byte)0) }, ignoreOrder: true);
    }

    [Fact]
    public async Task Compile_AllSemanticsRecognized_EmitsNoSd0104()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await new EffectCompiler().CompileAsync(ParameterizedShader, new CompilerOptions
        {
            Target = PlatformTarget.Vulkan,
            SourceFileName = "VulkanCleanSemantics.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        result.Value.Warnings.ShouldNotContain(w => w.Code == "SD0104",
            "POSITION0/TEXCOORD0 are both in the table; a false positive here would spam every clean build");
    }
}
