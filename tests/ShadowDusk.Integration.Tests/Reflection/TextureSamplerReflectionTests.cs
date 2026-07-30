#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Reflection;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

[Trait("Category", "Integration")]
[Trait("Platform", "DirectX")]
public sealed class TextureSamplerReflectionTests
{
    // Inlined from tests/fixtures/shaders/reflection/tex_sampler.hlsl
    private const string TexSamplerHlsl = """
        Texture2D    Albedo        : register(t0);
        SamplerState AlbedoSampler : register(s0);

        float4 PSMain(float2 uv : TEXCOORD0) : SV_Target
        {
            return Albedo.Sample(AlbedoSampler, uv);
        }
        """;

    private static async Task<ReadOnlyMemory<byte>> CompileToDxilAsync(string hlsl)
    {
        using var compiler = new DxcShaderCompiler();
        var request = new DxcCompileRequest
        {
            HlslSource     = hlsl,
            SourceFileName = "tex_sampler.hlsl",
            EntryPoint     = "PSMain",
            Stage          = ShaderStage.Pixel,
            Platform       = PlatformTarget.DirectX,
        };
        var result = await compiler.CompileAsync(request);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "compilation must succeed");
        return result.Value.Bytes;
    }

    [Fact]
    public async Task Reflect_TexSampler_ReturnsSuccess()
    {
        var dxilBlob = await CompileToDxilAsync(TexSamplerHlsl);

        var result = new DxilReflectionExtractor().Extract(dxilBlob);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Reflect_TexSampler_HasSingleTexture_NamedAlbedo()
    {
        var dxilBlob = await CompileToDxilAsync(TexSamplerHlsl);

        var reflected = new DxilReflectionExtractor().Extract(dxilBlob).Value;

        reflected.Textures.ShouldHaveSingleItem();
        reflected.Textures[0].Name.ShouldBe("Albedo");
    }

    [Fact]
    public async Task Reflect_TexSampler_Albedo_IsAtBindSlotZero()
    {
        var dxilBlob = await CompileToDxilAsync(TexSamplerHlsl);

        var reflected = new DxilReflectionExtractor().Extract(dxilBlob).Value;

        reflected.Textures[0].BindSlot.ShouldBe(0);
    }

    [Fact]
    public async Task Reflect_TexSampler_Albedo_DimensionIsTexture2D()
    {
        var dxilBlob = await CompileToDxilAsync(TexSamplerHlsl);

        var reflected = new DxilReflectionExtractor().Extract(dxilBlob).Value;

        reflected.Textures[0].Dimension.ShouldBe(TextureDimension.Texture2D);
    }

    [Fact]
    public async Task Reflect_TexSampler_HasSingleSampler_NamedAlbedoSampler()
    {
        var dxilBlob = await CompileToDxilAsync(TexSamplerHlsl);

        var reflected = new DxilReflectionExtractor().Extract(dxilBlob).Value;

        reflected.Samplers.ShouldHaveSingleItem();
        reflected.Samplers[0].Name.ShouldBe("AlbedoSampler");
    }

    [Fact]
    public async Task Reflect_TexSampler_AlbedoSampler_IsAtBindSlotZero()
    {
        var dxilBlob = await CompileToDxilAsync(TexSamplerHlsl);

        var reflected = new DxilReflectionExtractor().Extract(dxilBlob).Value;

        reflected.Samplers[0].BindSlot.ShouldBe(0);
    }

    [Fact]
    public async Task Reflect_TexSampler_NoCbuffers()
    {
        var dxilBlob = await CompileToDxilAsync(TexSamplerHlsl);

        var reflected = new DxilReflectionExtractor().Extract(dxilBlob).Value;

        reflected.ConstantBuffers.ShouldBeEmpty();
    }
}
