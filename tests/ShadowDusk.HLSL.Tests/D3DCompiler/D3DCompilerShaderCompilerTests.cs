#nullable enable

using System.Text;
using Shouldly;
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.Kind.ShouldBe(BlobKind.Dxbc);
        result.Value.Bytes.Length.ShouldBeGreaterThan(4);
        result.Value.Bytes.ToArray().Take(4).ShouldBe(Dxbc4cc);
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

        result.IsFailure.ShouldBeTrue();
        result.Error.Line.ShouldBeGreaterThan(0);
        result.Error.Message.ShouldNotBeNullOrEmpty();
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
        compileResult.IsSuccess.ShouldBeTrue();

        var extractor = new DxbcReflectionExtractor();
        var reflectResult = extractor.Extract(compileResult.Value.Bytes);

        reflectResult.IsSuccess.ShouldBeTrue();
        var effect = reflectResult.Value;

        effect.Textures.ShouldHaveSingleItem().Name.ShouldBe("SpriteTexture");
        effect.Samplers.ShouldHaveSingleItem().Name.ShouldBe("SpriteTextureSampler");

        // TintColor lives in the implicit $Globals cbuffer (size 16, one float4).
        effect.ConstantBuffers.ShouldHaveSingleItem();
        var cb = effect.ConstantBuffers[0];
        cb.SizeBytes.ShouldBe(16);
        cb.Variables.ShouldHaveSingleItem().Name.ShouldBe("TintColor");
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
        compileResult.IsSuccess.ShouldBeTrue();

        var pipeline = new DxbcReflectionPipeline(new DxbcReflectionExtractor());
        var result = await pipeline.ReflectAsync(compileResult.Value.Bytes, fxAnnotations: null);

        result.IsSuccess.ShouldBeTrue();
        // mgfxc folds the sampler into the texture: parameters are TintColor +
        // SpriteTexture only — NO standalone SpriteTextureSampler parameter.
        result.Value.Parameters.Select(p => p.Name)
            .ShouldBe(new[] { "TintColor", "SpriteTexture" }, ignoreOrder: true);
    }
}
