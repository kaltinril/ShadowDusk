#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Phase 54 — the empirical proof of the new DirectX12 target: compiles a shader with a
/// constant buffer AND a texture/sampler pair, then asserts the real MGFX profile byte (2,
/// matching MonoGame 3.8.5's <c>DirectX12ShaderProfile</c>), the <c>0xB00B00</c> wrapper
/// header, a DXBC-container-shaped DXIL payload, and non-empty structurally correct
/// reflection.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "DirectX12")]
public sealed class DirectX12EffectCompilerTests
{
    // Modern SM6-safe syntax throughout (Texture2D/SamplerState/.Sample()/SV_Target) —
    // legacy sampler2D/tex2D/COLOR is rejected by DXC at DirectX12's forced vs_6_0/ps_6_0.
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
    public async Task Compile_ParameterizedShader_ProducesRealDirectX12Container()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await new EffectCompiler().CompileAsync(ParameterizedShader, new CompilerOptions
        {
            Target = PlatformTarget.DirectX12,
            SourceFileName = "DirectX12Parameterized.fx",
        }, cts.Token);

        result.IsSuccess.Should().BeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        reader.ProfileId.Should().Be(2, "MgfxProfile.DirectX12 is 2, matching real MonoGame 3.8.5's DirectX12ShaderProfile");
        reader.MgfxVersion.Should().Be(11, "DirectX12 always writes the v11 shader-record shape");

        reader.Shaders.Should().HaveCount(2, "one vertex + one pixel shader");
        foreach (var shader in reader.Shaders)
        {
            var dx12 = DirectX12ShaderCodeReader.Parse(shader.Bytecode);
            dx12.DxilMagicOk.Should().BeTrue($"shader #{shader.Index} (isVertex={shader.IsVertex}) must wrap a DXBC-container-shaped DXIL blob");
        }

        reader.ConstantBuffers.Should().NotBeEmpty("Tint must be reflected into a constant buffer");
        reader.Samplers.Should().NotBeEmpty("SpriteTexture/SpriteTextureSampler must be reflected");
        reader.ParameterNames.Should().Contain("Tint");

        var pixelShader = reader.Shaders.Single(s => !s.IsVertex);
        pixelShader.ConstantBufferIndices.Should().NotBeEmpty("the pixel shader stage must bind its constant buffer");
    }
}
