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
public sealed class StructReflectionTests
{
    // Inlined from tests/fixtures/shaders/reflection/struct_cbuffer.hlsl
    private const string StructCbufferHlsl = """
        struct DirectionalLight
        {
            float3 Dir;
            float3 Color;
            float  Intensity;
        };

        cbuffer LightParams : register(b0)
        {
            DirectionalLight Light;
        }

        float4 PSMain() : SV_Target
        {
            return float4(Light.Color * Light.Intensity, 1.0);
        }
        """;

    private static async Task<ReadOnlyMemory<byte>> CompileToDxilAsync(string hlsl)
    {
        using var compiler = new DxcShaderCompiler();
        var request = new DxcCompileRequest
        {
            HlslSource     = hlsl,
            SourceFileName = "struct_cbuffer.hlsl",
            EntryPoint     = "PSMain",
            Stage          = ShaderStage.Pixel,
            Platform       = PlatformTarget.DirectX,
        };
        var result = await compiler.CompileAsync(request);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "compilation must succeed");
        return result.Value.Bytes;
    }

    [Fact]
    public async Task Reflect_StructCbuffer_ReturnsSuccess()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var result = new DxilReflectionExtractor().Extract(dxilBlob);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Reflect_StructCbuffer_HasSingleCbufferNamedLightParams()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var reflected = new DxilReflectionExtractor().Extract(dxilBlob).Value;

        reflected.ConstantBuffers.ShouldHaveSingleItem();
        reflected.ConstantBuffers[0].Name.ShouldBe("LightParams");
    }

    [Fact]
    public async Task Reflect_StructCbuffer_Light_IsClassStruct()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var cbuffer = new DxilReflectionExtractor().Extract(dxilBlob).Value.ConstantBuffers[0];
        var light = cbuffer.Variables.Single(v => v.Name == "Light");

        light.ParameterClass.ShouldBe(EffectParameterClass.Struct);
    }

    [Fact]
    public async Task Reflect_StructCbuffer_Light_HasThreeMembers()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var cbuffer = new DxilReflectionExtractor().Extract(dxilBlob).Value.ConstantBuffers[0];
        var light = cbuffer.Variables.Single(v => v.Name == "Light");

        light.Members!.Count().ShouldBe(3);
    }

    [Fact]
    public async Task Reflect_StructCbuffer_Light_Dir_IsFloat3Vector()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var cbuffer = new DxilReflectionExtractor().Extract(dxilBlob).Value.ConstantBuffers[0];
        var light = cbuffer.Variables.Single(v => v.Name == "Light");
        var dir = light.Members!.Single(m => m.Name == "Dir");

        dir.ParameterClass.ShouldBe(EffectParameterClass.Vector);
        dir.ParameterType.ShouldBe(EffectParameterType.Single);
        dir.Columns.ShouldBe(3);
    }

    [Fact]
    public async Task Reflect_StructCbuffer_Light_Color_IsFloat3Vector()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var cbuffer = new DxilReflectionExtractor().Extract(dxilBlob).Value.ConstantBuffers[0];
        var light = cbuffer.Variables.Single(v => v.Name == "Light");
        var color = light.Members!.Single(m => m.Name == "Color");

        color.ParameterClass.ShouldBe(EffectParameterClass.Vector);
        color.ParameterType.ShouldBe(EffectParameterType.Single);
        color.Columns.ShouldBe(3);
    }

    [Fact]
    public async Task Reflect_StructCbuffer_Light_Intensity_IsScalarSingle()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var cbuffer = new DxilReflectionExtractor().Extract(dxilBlob).Value.ConstantBuffers[0];
        var light = cbuffer.Variables.Single(v => v.Name == "Light");
        var intensity = light.Members!.Single(m => m.Name == "Intensity");

        intensity.ParameterClass.ShouldBe(EffectParameterClass.Scalar);
        intensity.ParameterType.ShouldBe(EffectParameterType.Single);
    }
}
