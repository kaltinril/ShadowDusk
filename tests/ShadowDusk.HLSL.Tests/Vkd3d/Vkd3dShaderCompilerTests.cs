#nullable enable

using System.Text;
using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Reflection;
using ShadowDusk.HLSL.Vkd3d;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Vkd3d;

/// <summary>
/// Tests for the cross-platform vkd3d-shader DXBC backend (Phase 18 Track A).
/// The binding is cross-platform; the live-compile tests are gated on the native
/// vkd3d-shader library being present (availability-probed via
/// <see cref="Vkd3dFactAttribute"/> — tools/restore provisions the per-RID binary,
/// Phase 37 C), so they run on every OS in CI. Tagged Integration because they
/// exercise native interop.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Vkd3dShaderCompilerTests
{
    private const string TexturedPixelShader = """
        Texture2D SpriteTexture;
        SamplerState SpriteTextureSampler;
        float4 TintColor;

        struct PSInput { float4 Position : SV_POSITION; float2 Tex : TEXCOORD0; };

        float4 MainPS(PSInput input) : SV_TARGET
        {
            return SpriteTexture.Sample(SpriteTextureSampler, input.Tex) * TintColor;
        }
        """;

    private static byte[] Dxbc4cc => Encoding.ASCII.GetBytes("DXBC");

    [Vkd3dFact]
    public async Task Compile_ProducesDxbcContainer()
    {
        var compiler = new Vkd3dShaderCompiler();

        var result = await compiler.CompileAsync(new D3DCompileRequest
        {
            HlslSource     = TexturedPixelShader,
            SourceFileName = "test.hlsl",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
            AllowWarnings  = true,
        });

        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? result.Error.Message : "vkd3d should compile a valid PS");
        result.Value.Kind.Should().Be(BlobKind.Dxbc);
        result.Value.Bytes.Length.Should().BeGreaterThan(4);
        // DXBC_TPF is a standard DXBC container — fourcc "DXBC".
        result.Value.Bytes.ToArray().Take(4).Should().Equal(Dxbc4cc);
    }

    [Vkd3dFact]
    public async Task Compile_InvalidSource_SurfacesDiagnosticNotSwallowed()
    {
        var compiler = new Vkd3dShaderCompiler();

        var result = await compiler.CompileAsync(new D3DCompileRequest
        {
            HlslSource     = "float4 MainPS() : SV_TARGET { return undeclared_symbol; }",
            SourceFileName = "bad.hlsl",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
        });

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().NotBeNullOrEmpty();
    }

    [Vkd3dFact(requiresD3DReflect: true)]
    public async Task DxbcReflectionExtractor_ReflectsVkd3dOutput()
    {
        // Confirms the SAME DxbcReflectionExtractor (ID3D11ShaderReflection) reflects
        // vkd3d's DXBC_TPF output cleanly — no separate reflector needed.
        var compiler = new Vkd3dShaderCompiler();
        var compileResult = await compiler.CompileAsync(new D3DCompileRequest
        {
            HlslSource     = TexturedPixelShader,
            SourceFileName = "test.hlsl",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
            AllowWarnings  = true,
        });
        compileResult.IsSuccess.Should().BeTrue(
            because: compileResult.IsFailure ? compileResult.Error.Message : "vkd3d should compile");

        var extractor = new DxbcReflectionExtractor();
        var reflectResult = extractor.Extract(compileResult.Value.Bytes);

        reflectResult.IsSuccess.Should().BeTrue(
            because: reflectResult.IsFailure ? reflectResult.Error.Message : "D3DReflect should accept vkd3d DXBC");
        var effect = reflectResult.Value;

        effect.Textures.Select(t => t.Name).Should().Contain("SpriteTexture");
        effect.Samplers.Select(s => s.Name).Should().Contain("SpriteTextureSampler");
        effect.ConstantBuffers.SelectMany(c => c.Variables).Select(v => v.Name)
            .Should().Contain("TintColor");
    }
}
