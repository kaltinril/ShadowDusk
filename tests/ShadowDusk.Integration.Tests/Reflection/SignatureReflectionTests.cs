#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Reflection;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

[Trait("Category", "Integration")]
[Trait("Platform", "DirectX")]
public sealed class SignatureReflectionTests
{
    // Inlined from tests/fixtures/shaders/reflection/vs_input.hlsl
    private const string VsInputHlsl = """
        struct VSInput
        {
            float3 Position : POSITION;
            float3 Normal   : NORMAL;
            float2 TexCoord : TEXCOORD0;
        };

        float4 VSMain(VSInput input) : SV_Position
        {
            return float4(input.Position, 1.0);
        }
        """;

    private static async Task<ReadOnlyMemory<byte>> CompileToDxilAsync(string hlsl)
    {
        using var compiler = new DxcShaderCompiler();
        var request = new DxcCompileRequest
        {
            HlslSource     = hlsl,
            SourceFileName = "vs_input.hlsl",
            EntryPoint     = "VSMain",
            Stage          = ShaderStage.Vertex,
            Platform       = PlatformTarget.DirectX,
        };
        var result = await compiler.CompileAsync(request);
        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? result.Error.FxcFormattedMessage : "compilation must succeed");
        return result.Value.Bytes;
    }

    [Fact]
    public async Task Reflect_VsInput_ReturnsSuccess()
    {
        var dxilBlob = await CompileToDxilAsync(VsInputHlsl);

        var result = new DxilReflectionExtractor().Extract(dxilBlob);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Reflect_VsInput_InputSignatureHasThreeElements()
    {
        var dxilBlob = await CompileToDxilAsync(VsInputHlsl);

        var reflected = new DxilReflectionExtractor().Extract(dxilBlob).Value;

        reflected.InputSignature.Should().HaveCount(3);
    }

    [Fact]
    public async Task Reflect_VsInput_InputSignatureContainsPosition()
    {
        var dxilBlob = await CompileToDxilAsync(VsInputHlsl);

        var reflected = new DxilReflectionExtractor().Extract(dxilBlob).Value;

        reflected.InputSignature.Should().Contain(p =>
            p.SemanticName == "POSITION" && p.SemanticIndex == 0);
    }

    [Fact]
    public async Task Reflect_VsInput_InputSignatureContainsNormal()
    {
        var dxilBlob = await CompileToDxilAsync(VsInputHlsl);

        var reflected = new DxilReflectionExtractor().Extract(dxilBlob).Value;

        reflected.InputSignature.Should().Contain(p =>
            p.SemanticName == "NORMAL" && p.SemanticIndex == 0);
    }

    [Fact]
    public async Task Reflect_VsInput_InputSignatureContainsTexcoord()
    {
        var dxilBlob = await CompileToDxilAsync(VsInputHlsl);

        var reflected = new DxilReflectionExtractor().Extract(dxilBlob).Value;

        reflected.InputSignature.Should().Contain(p =>
            p.SemanticName == "TEXCOORD" && p.SemanticIndex == 0);
    }
}
