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
public sealed class BasicCbufferReflectionTests
{
    // Inlined from tests/fixtures/shaders/reflection/basic_cbuffer.hlsl
    private const string BasicCbufferHlsl = """
        cbuffer Params : register(b0)
        {
            float    Scale;
            float3   Direction;
            float4   Color;
            float4x4 World;
        }

        float4 PSMain() : SV_Target { return Color * Scale; }
        """;

    private static async Task<ReadOnlyMemory<byte>> CompileToDxilAsync(
        string hlsl, string entryPoint, ShaderStage stage)
    {
        using var compiler = new DxcShaderCompiler();
        var request = new DxcCompileRequest
        {
            HlslSource     = hlsl,
            SourceFileName = "basic_cbuffer.hlsl",
            EntryPoint     = entryPoint,
            Stage          = stage,
            Platform       = PlatformTarget.DirectX,
        };
        var result = await compiler.CompileAsync(request);
        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? result.Error.FxcFormattedMessage : "compilation must succeed");
        return result.Value.Bytes;
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_ReturnsSuccess()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var result = extractor.Extract(dxilBlob);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_HasSingleCbufferNamedParams()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var reflected = extractor.Extract(dxilBlob).Value;

        reflected.ConstantBuffers.Should().ContainSingle();
        reflected.ConstantBuffers[0].Name.Should().Be("Params");
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_HasFourVariables()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var cbuffer = extractor.Extract(dxilBlob).Value.ConstantBuffers[0];

        cbuffer.Variables.Should().HaveCount(4);
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_Scale_IsScalarSingleAtOffsetZero()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var variables = extractor.Extract(dxilBlob).Value.ConstantBuffers[0].Variables;

        var scale = variables.Single(v => v.Name == "Scale");
        scale.StartOffset.Should().Be(0);
        scale.SizeBytes.Should().Be(4);
        scale.ParameterClass.Should().Be(EffectParameterClass.Scalar);
        scale.ParameterType.Should().Be(EffectParameterType.Single);
        scale.Columns.Should().Be(1);
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_Direction_IsFloat3AtOffset4()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var variables = extractor.Extract(dxilBlob).Value.ConstantBuffers[0].Variables;

        var direction = variables.Single(v => v.Name == "Direction");
        direction.StartOffset.Should().Be(4);
        direction.SizeBytes.Should().Be(12);
        direction.ParameterClass.Should().Be(EffectParameterClass.Vector);
        direction.ParameterType.Should().Be(EffectParameterType.Single);
        direction.Columns.Should().Be(3);
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_Color_IsFloat4AtOffset16()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var variables = extractor.Extract(dxilBlob).Value.ConstantBuffers[0].Variables;

        var color = variables.Single(v => v.Name == "Color");
        color.StartOffset.Should().Be(16);
        color.SizeBytes.Should().Be(16);
        color.ParameterClass.Should().Be(EffectParameterClass.Vector);
        color.ParameterType.Should().Be(EffectParameterType.Single);
        color.Columns.Should().Be(4);
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_World_IsFloat4x4AtOffset32()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var variables = extractor.Extract(dxilBlob).Value.ConstantBuffers[0].Variables;

        var world = variables.Single(v => v.Name == "World");
        world.StartOffset.Should().Be(32);
        world.SizeBytes.Should().Be(64);
        world.ParameterClass.Should().Be(EffectParameterClass.Matrix);
        world.ParameterType.Should().Be(EffectParameterType.Single);
        world.Rows.Should().Be(4);
        world.Columns.Should().Be(4);
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_TotalSize_IsNinetySixBytes()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var cbuffer = extractor.Extract(dxilBlob).Value.ConstantBuffers[0];

        cbuffer.SizeBytes.Should().Be(96);
    }
}
