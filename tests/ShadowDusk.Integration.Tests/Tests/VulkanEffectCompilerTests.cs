#nullable enable

using FluentAssertions;
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

        result.IsSuccess.Should().BeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        reader.ProfileId.Should().Be(80, "MgfxProfile.Vulkan is 80, matching real MonoGame 3.8.5 (not the old wrong placeholder 3)");
        reader.MgfxVersion.Should().Be(11, "Vulkan always writes the v11 shader-record shape");

        reader.Shaders.Should().HaveCount(2, "one vertex + one pixel shader");
        foreach (var shader in reader.Shaders)
        {
            var vk = VulkanShaderCodeReader.Parse(shader.Bytecode);
            vk.SpirvMagicOk.Should().BeTrue($"shader #{shader.Index} (isVertex={shader.IsVertex}) must wrap valid SPIR-V");
        }

        // The pixel shader reflects both the texture/sampler AND the Tint parameter
        // (packed into $Globals) — this is exactly what the reflection-gate fix
        // (CompilationPipeline.cs) makes possible; before it, every Vulkan shader's
        // reflection was silently empty regardless of what the HLSL declared.
        reader.ConstantBuffers.Should().NotBeEmpty("Tint must be reflected into a constant buffer");
        reader.Samplers.Should().NotBeEmpty("SpriteTexture/SpriteTextureSampler must be reflected");
        reader.ParameterNames.Should().Contain("Tint");

        var pixelShader = reader.Shaders.Single(s => !s.IsVertex);
        pixelShader.ConstantBufferIndices.Should().NotBeEmpty("the pixel shader stage must bind its constant buffer");
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

        result.ExitCode.Should().Be(0, result.Stderr);

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        var pixelShader = reader.Shaders.Single(s => !s.IsVertex);
        var vk = VulkanShaderCodeReader.Parse(pixelShader.Bytecode);

        vk.Bindings.Should().Contain(
            b => b.DescriptorType == 8 && b.Binding == 0,
            "the implicit $Globals cbuffer (VkDescriptorType UNIFORM_BUFFER_DYNAMIC=8) must always bind to 0, " +
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

        result.IsFailure.Should().BeTrue("Vulkan does not support more than one constant buffer per shader stage");
        result.Error.Select(e => e.Code).Should().Contain("SD0026");
    }
}
