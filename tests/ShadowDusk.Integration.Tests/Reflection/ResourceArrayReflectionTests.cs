#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Reflection;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// Issue #324: both reflectors ShadowDusk ships for the DXC targets must see an ARRAY of
/// textures or samplers and report its element count, on real DXC output. The pure-managed
/// <see cref="SpirvReflector"/> (Vulkan, and the browser's OpenGL) reads the
/// <c>OpTypeArray</c>; the native <see cref="DxilReflectionExtractor"/> (DirectX 12, desktop
/// OpenGL) reads the binding's <c>BindCount</c>. Before the fix the SPIR-V reflector dropped
/// the texture entirely and the DXIL one reported it as a single texture.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ResourceArrayReflectionTests
{
    private static string Shader(int elements, bool samplerArray) =>
        $"Texture2D Tex[{elements}] : register(t0);\n" +
        (samplerArray ? $"SamplerState TexSampler[{elements}] : register(s0);\n" : "SamplerState TexSampler : register(s0);\n") +
        "float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0\n{\n    float4 c = 0" +
        string.Concat(Enumerable.Range(0, elements).Select(i => $" + Tex[{i}].Sample(TexSampler{(samplerArray ? $"[{i}]" : "")}, uv)")) +
        ";\n    return c;\n}\n";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task SpirvReflector_ReportsTheTextureArray_WithItsElementCount(int elements)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        ReadOnlyMemory<byte> spirv = await CompileAsync(Shader(elements, samplerArray: false), PlatformTarget.Vulkan, cts.Token);

        var result = new SpirvReflector().Reflect(spirv);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "ok");
        TextureReflection tex = result.Value.Textures.ShouldHaveSingleItem();
        tex.Name.ShouldBe("Tex");
        tex.ArrayLength.ShouldBe(elements, "a 1-element array is still an array in SPIR-V (OpTypeArray of length 1)");
        tex.Dimension.ShouldBe(TextureDimension.Texture2D);
        result.Value.Samplers.ShouldHaveSingleItem().ArrayLength.ShouldBeNull();
    }

    [Fact]
    public async Task SpirvReflector_ReportsTheSamplerArray_WithItsElementCount()
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        ReadOnlyMemory<byte> spirv = await CompileAsync(Shader(2, samplerArray: true), PlatformTarget.Vulkan, cts.Token);

        var result = new SpirvReflector().Reflect(spirv);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "ok");
        result.Value.Textures.ShouldHaveSingleItem().ArrayLength.ShouldBe(2);
        SamplerReflection samp = result.Value.Samplers.ShouldHaveSingleItem();
        samp.Name.ShouldBe("TexSampler");
        samp.ArrayLength.ShouldBe(2);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(1, null)]
    public async Task DxilReflection_ReportsTheTextureArray_FromBindCount(int elements, int? expectedArrayLength)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        ReadOnlyMemory<byte> dxil = await CompileAsync(Shader(elements, samplerArray: false), PlatformTarget.DirectX12, cts.Token);

        var result = new DxilReflectionExtractor().Extract(dxil, cts.Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "ok");
        TextureReflection tex = result.Value.Textures.ShouldHaveSingleItem();
        tex.Name.ShouldBe("Tex");
        tex.BindSlot.ShouldBe(0, "one SM6 binding spanning the whole array from t0");
        tex.ArrayLength.ShouldBe(expectedArrayLength, "DXIL carries BindCount, so a 1-element array is indistinguishable from a single texture");
        result.Value.Samplers.ShouldHaveSingleItem().ArrayLength.ShouldBeNull();
    }

    [Fact]
    public async Task DxilReflection_ReportsTheSamplerArray_FromBindCount()
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        ReadOnlyMemory<byte> dxil = await CompileAsync(Shader(2, samplerArray: true), PlatformTarget.DirectX12, cts.Token);

        var result = new DxilReflectionExtractor().Extract(dxil, cts.Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "ok");
        result.Value.Samplers.ShouldHaveSingleItem().ArrayLength.ShouldBe(2);
    }

    private static async Task<ReadOnlyMemory<byte>> CompileAsync(string hlsl, PlatformTarget platform, CancellationToken ct)
    {
        using var compiler = new DxcShaderCompiler();
        var request = new DxcCompileRequest
        {
            HlslSource     = hlsl,
            SourceFileName = "ResourceArray.hlsl",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
            Platform       = platform,
            Options        = new DxcCompileOptions { AllowWarnings = true },
        };
        var result = await compiler.CompileAsync(request, ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "compilation must succeed");
        return result.Value.Bytes;
    }
}
