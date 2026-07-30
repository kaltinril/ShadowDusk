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
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "compilation must succeed");
        return result.Value.Bytes;
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_ReturnsSuccess()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var result = extractor.Extract(dxilBlob);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_HasSingleCbufferNamedParams()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var reflected = extractor.Extract(dxilBlob).Value;

        reflected.ConstantBuffers.ShouldHaveSingleItem();
        reflected.ConstantBuffers[0].Name.ShouldBe("Params");
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_HasFourVariables()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var cbuffer = extractor.Extract(dxilBlob).Value.ConstantBuffers[0];

        cbuffer.Variables.Count().ShouldBe(4);
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_Scale_IsScalarSingleAtOffsetZero()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var variables = extractor.Extract(dxilBlob).Value.ConstantBuffers[0].Variables;

        var scale = variables.Single(v => v.Name == "Scale");
        scale.StartOffset.ShouldBe(0);
        scale.SizeBytes.ShouldBe(4);
        scale.ParameterClass.ShouldBe(EffectParameterClass.Scalar);
        scale.ParameterType.ShouldBe(EffectParameterType.Single);
        scale.Columns.ShouldBe(1);
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_Direction_IsFloat3AtOffset4()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var variables = extractor.Extract(dxilBlob).Value.ConstantBuffers[0].Variables;

        var direction = variables.Single(v => v.Name == "Direction");
        direction.StartOffset.ShouldBe(4);
        direction.SizeBytes.ShouldBe(12);
        direction.ParameterClass.ShouldBe(EffectParameterClass.Vector);
        direction.ParameterType.ShouldBe(EffectParameterType.Single);
        direction.Columns.ShouldBe(3);
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_Color_IsFloat4AtOffset16()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var variables = extractor.Extract(dxilBlob).Value.ConstantBuffers[0].Variables;

        var color = variables.Single(v => v.Name == "Color");
        color.StartOffset.ShouldBe(16);
        color.SizeBytes.ShouldBe(16);
        color.ParameterClass.ShouldBe(EffectParameterClass.Vector);
        color.ParameterType.ShouldBe(EffectParameterType.Single);
        color.Columns.ShouldBe(4);
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_World_IsFloat4x4AtOffset32()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var variables = extractor.Extract(dxilBlob).Value.ConstantBuffers[0].Variables;

        var world = variables.Single(v => v.Name == "World");
        world.StartOffset.ShouldBe(32);
        world.SizeBytes.ShouldBe(64);
        world.ParameterClass.ShouldBe(EffectParameterClass.Matrix);
        world.ParameterType.ShouldBe(EffectParameterType.Single);
        world.Rows.ShouldBe(4);
        world.Columns.ShouldBe(4);
    }

    [Fact]
    public async Task Reflect_BasicCbuffer_TotalSize_IsNinetySixBytes()
    {
        var dxilBlob = await CompileToDxilAsync(BasicCbufferHlsl, "PSMain", ShaderStage.Pixel);

        var extractor = new DxilReflectionExtractor();
        var cbuffer = extractor.Extract(dxilBlob).Value.ConstantBuffers[0];

        cbuffer.SizeBytes.ShouldBe(96);
    }
}
