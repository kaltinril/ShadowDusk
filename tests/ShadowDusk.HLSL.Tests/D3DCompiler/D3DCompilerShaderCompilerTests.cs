#nullable enable

using System.Text;
using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Reflection;
using Xunit;

namespace ShadowDusk.HLSL.Tests.D3DCompiler;

/// <summary>
/// Tests for the d3dcompiler_47 DXBC backend and its DXBC reflection. Native
/// interop, so tagged Integration and skipped off Windows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class D3DCompilerShaderCompilerTests
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

    [WindowsFact]
    public async Task Compile_ProducesDxbcBytecode()
    {
        var compiler = new D3DCompilerShaderCompiler();

        var result = await compiler.CompileAsync(new D3DCompileRequest
        {
            HlslSource     = TexturedPixelShader,
            SourceFileName = "test.hlsl",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
            AllowWarnings  = true,
        });

        result.IsSuccess.Should().BeTrue();
        result.Value.Kind.Should().Be(BlobKind.Dxbc);
        result.Value.Bytes.Length.Should().BeGreaterThan(4);
        result.Value.Bytes.ToArray().Take(4).Should().Equal(Dxbc4cc);
    }

    [WindowsFact]
    public async Task Compile_InvalidSource_SurfacesDiagnosticVerbatim()
    {
        var compiler = new D3DCompilerShaderCompiler();

        var result = await compiler.CompileAsync(new D3DCompileRequest
        {
            HlslSource     = "float4 MainPS() : SV_TARGET { return undeclared_symbol; }",
            SourceFileName = "bad.hlsl",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
        });

        result.IsFailure.Should().BeTrue();
        result.Error.Line.Should().BeGreaterThan(0);
        result.Error.Message.Should().NotBeNullOrEmpty();
    }

    [WindowsFact]
    public async Task Reflect_ExtractsCbufferTextureAndSampler()
    {
        var compiler = new D3DCompilerShaderCompiler();
        var compileResult = await compiler.CompileAsync(new D3DCompileRequest
        {
            HlslSource     = TexturedPixelShader,
            SourceFileName = "test.hlsl",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
            AllowWarnings  = true,
        });
        compileResult.IsSuccess.Should().BeTrue();

        var extractor = new DxbcReflectionExtractor();
        var reflectResult = extractor.Extract(compileResult.Value.Bytes);

        reflectResult.IsSuccess.Should().BeTrue();
        var effect = reflectResult.Value;

        effect.Textures.Should().ContainSingle()
            .Which.Name.Should().Be("SpriteTexture");
        effect.Samplers.Should().ContainSingle()
            .Which.Name.Should().Be("SpriteTextureSampler");

        // TintColor lives in the implicit $Globals cbuffer (size 16, one float4).
        effect.ConstantBuffers.Should().ContainSingle();
        var cb = effect.ConstantBuffers[0];
        cb.SizeBytes.Should().Be(16);
        cb.Variables.Should().ContainSingle()
            .Which.Name.Should().Be("TintColor");
    }

    [WindowsFact]
    public async Task ReflectionPipeline_DropsStandaloneSamplerParameter()
    {
        var compiler = new D3DCompilerShaderCompiler();
        var compileResult = await compiler.CompileAsync(new D3DCompileRequest
        {
            HlslSource     = TexturedPixelShader,
            SourceFileName = "test.hlsl",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
            AllowWarnings  = true,
        });
        compileResult.IsSuccess.Should().BeTrue();

        var pipeline = new DxbcReflectionPipeline(new DxbcReflectionExtractor());
        var result = await pipeline.ReflectAsync(compileResult.Value.Bytes, fxAnnotations: null);

        result.IsSuccess.Should().BeTrue();
        // mgfxc folds the sampler into the texture: parameters are TintColor +
        // SpriteTexture only — NO standalone SpriteTextureSampler parameter.
        result.Value.Parameters.Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "TintColor", "SpriteTexture" });
    }
}
