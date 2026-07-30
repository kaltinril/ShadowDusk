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
public sealed class ArrayReflectionTests
{
    // Inlined from tests/fixtures/shaders/reflection/array_param.hlsl
    private const string ArrayParamHlsl = """
        cbuffer Params : register(b0)
        {
            float PointLights[4];
        }

        float4 PSMain() : SV_Target { return float4(PointLights[0], 0, 0, 1); }
        """;

    private static async Task<ReadOnlyMemory<byte>> CompileToDxilAsync(string hlsl)
    {
        using var compiler = new DxcShaderCompiler();
        var request = new DxcCompileRequest
        {
            HlslSource     = hlsl,
            SourceFileName = "array_param.hlsl",
            EntryPoint     = "PSMain",
            Stage          = ShaderStage.Pixel,
            Platform       = PlatformTarget.DirectX,
        };
        var result = await compiler.CompileAsync(request);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "compilation must succeed");
        return result.Value.Bytes;
    }

    [Fact]
    public async Task Reflect_ArrayParam_ReturnsSuccess()
    {
        var dxilBlob = await CompileToDxilAsync(ArrayParamHlsl);

        var result = new DxilReflectionExtractor().Extract(dxilBlob);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Reflect_ArrayParam_HasSingleCbuffer()
    {
        var dxilBlob = await CompileToDxilAsync(ArrayParamHlsl);

        var reflected = new DxilReflectionExtractor().Extract(dxilBlob).Value;

        reflected.ConstantBuffers.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Reflect_ArrayParam_PointLights_HasFourElements()
    {
        var dxilBlob = await CompileToDxilAsync(ArrayParamHlsl);

        var cbuffer = new DxilReflectionExtractor().Extract(dxilBlob).Value.ConstantBuffers[0];
        var variable = cbuffer.Variables.Single(v => v.Name == "PointLights");

        variable.Elements.ShouldBe(4);
    }

    [Fact]
    public async Task Reflect_ArrayParam_PointLights_SizeIsSixtyFourBytes()
    {
        // float[4] in a cbuffer: each float is padded to a 16-byte slot → 4 × 16 = 64.
        var dxilBlob = await CompileToDxilAsync(ArrayParamHlsl);

        var cbuffer = new DxilReflectionExtractor().Extract(dxilBlob).Value.ConstantBuffers[0];
        var variable = cbuffer.Variables.Single(v => v.Name == "PointLights");

        variable.SizeBytes.ShouldBe(64);
    }

    [Fact]
    public async Task Reflect_ArrayParam_PointLights_IsScalarSingle()
    {
        var dxilBlob = await CompileToDxilAsync(ArrayParamHlsl);

        var cbuffer = new DxilReflectionExtractor().Extract(dxilBlob).Value.ConstantBuffers[0];
        var variable = cbuffer.Variables.Single(v => v.Name == "PointLights");

        variable.ParameterClass.ShouldBe(EffectParameterClass.Scalar);
        variable.ParameterType.ShouldBe(EffectParameterType.Single);
    }
}
