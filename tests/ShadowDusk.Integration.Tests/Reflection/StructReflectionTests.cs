#nullable enable

using FluentAssertions;
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
        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? result.Error.FxcFormattedMessage : "compilation must succeed");
        return result.Value.Bytes;
    }

    [Fact]
    public async Task Reflect_StructCbuffer_ReturnsSuccess()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var result = new DxilReflectionExtractor().Extract(dxilBlob);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Reflect_StructCbuffer_HasSingleCbufferNamedLightParams()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var reflected = new DxilReflectionExtractor().Extract(dxilBlob).Value;

        reflected.ConstantBuffers.Should().ContainSingle();
        reflected.ConstantBuffers[0].Name.Should().Be("LightParams");
    }

    [Fact]
    public async Task Reflect_StructCbuffer_Light_IsClassStruct()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var cbuffer = new DxilReflectionExtractor().Extract(dxilBlob).Value.ConstantBuffers[0];
        var light = cbuffer.Variables.Single(v => v.Name == "Light");

        light.ParameterClass.Should().Be(EffectParameterClass.Struct);
    }

    [Fact]
    public async Task Reflect_StructCbuffer_Light_HasThreeMembers()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var cbuffer = new DxilReflectionExtractor().Extract(dxilBlob).Value.ConstantBuffers[0];
        var light = cbuffer.Variables.Single(v => v.Name == "Light");

        light.Members.Should().HaveCount(3);
    }

    [Fact]
    public async Task Reflect_StructCbuffer_Light_Dir_IsFloat3Vector()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var cbuffer = new DxilReflectionExtractor().Extract(dxilBlob).Value.ConstantBuffers[0];
        var light = cbuffer.Variables.Single(v => v.Name == "Light");
        var dir = light.Members!.Single(m => m.Name == "Dir");

        dir.ParameterClass.Should().Be(EffectParameterClass.Vector);
        dir.ParameterType.Should().Be(EffectParameterType.Single);
        dir.Columns.Should().Be(3);
    }

    [Fact]
    public async Task Reflect_StructCbuffer_Light_Color_IsFloat3Vector()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var cbuffer = new DxilReflectionExtractor().Extract(dxilBlob).Value.ConstantBuffers[0];
        var light = cbuffer.Variables.Single(v => v.Name == "Light");
        var color = light.Members!.Single(m => m.Name == "Color");

        color.ParameterClass.Should().Be(EffectParameterClass.Vector);
        color.ParameterType.Should().Be(EffectParameterType.Single);
        color.Columns.Should().Be(3);
    }

    [Fact]
    public async Task Reflect_StructCbuffer_Light_Intensity_IsScalarSingle()
    {
        var dxilBlob = await CompileToDxilAsync(StructCbufferHlsl);

        var cbuffer = new DxilReflectionExtractor().Extract(dxilBlob).Value.ConstantBuffers[0];
        var light = cbuffer.Variables.Single(v => v.Name == "Light");
        var intensity = light.Members!.Single(m => m.Name == "Intensity");

        intensity.ParameterClass.Should().Be(EffectParameterClass.Scalar);
        intensity.ParameterType.Should().Be(EffectParameterType.Single);
    }
}
