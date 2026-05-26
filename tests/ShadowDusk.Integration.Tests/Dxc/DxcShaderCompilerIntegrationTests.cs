#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.Integration.Tests.Dxc;

[Trait("Category", "Integration")]
public sealed class DxcShaderCompilerIntegrationTests
{
    private static DxcCompileRequest VertexRequest(string hlsl, PlatformTarget platform, string entry = "VSMain")
        => new()
        {
            HlslSource = hlsl,
            SourceFileName = "test.fx",
            EntryPoint = entry,
            Stage = ShaderStage.Vertex,
            Platform = platform,
        };

    private static DxcCompileRequest PixelRequest(string hlsl, PlatformTarget platform, string entry = "PSMain")
        => new()
        {
            HlslSource = hlsl,
            SourceFileName = "test.fx",
            EntryPoint = entry,
            Stage = ShaderStage.Pixel,
            Platform = platform,
        };

    private const string MinimalVs = "float4 VSMain(float4 pos : POSITION) : SV_Position { return pos; }";
    private const string MinimalPs = "float4 PSMain() : SV_Target { return float4(1,0,0,1); }";

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task CompileMinimalVertex_OpenGL_ReturnsSpirvBlob()
    {
        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(VertexRequest(MinimalVs, PlatformTarget.OpenGL));

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.FxcFormattedMessage : "");
        result.Value.Kind.Should().Be(BlobKind.Spirv);
        result.Value.Bytes.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task CompileMinimalPixel_OpenGL_ReturnsSpirvBlob()
    {
        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(PixelRequest(MinimalPs, PlatformTarget.OpenGL));

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.FxcFormattedMessage : "");
        result.Value.Kind.Should().Be(BlobKind.Spirv);
        result.Value.Bytes.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Platform", "DirectX")]
    public async Task CompileMinimalVertex_DirectX_ReturnsDxbcBlob()
    {
        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(VertexRequest(MinimalVs, PlatformTarget.DirectX));

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.FxcFormattedMessage : "");
        result.Value.Kind.Should().Be(BlobKind.Dxbc);
        result.Value.Bytes.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Platform", "Vulkan")]
    public async Task CompileVulkanVertex_ReturnsSpirvBlob()
    {
        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(VertexRequest(MinimalVs, PlatformTarget.Vulkan));

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.FxcFormattedMessage : "");
        result.Value.Kind.Should().Be(BlobKind.Spirv);
        result.Value.Bytes.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task SyntaxError_ReturnsFailureWithFxcFormattedMessage()
    {
        // Missing closing brace — definite syntax error
        const string badHlsl = "float4 VSMain(float4 pos : POSITION) : SV_Position { return pos;";

        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(VertexRequest(badHlsl, PlatformTarget.OpenGL));

        result.IsFailure.Should().BeTrue();
        result.Error.FxcFormattedMessage.Should().Contain("(");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task UndefinedVariable_ReturnsFailureWithLineCol()
    {
        const string badHlsl = "float4 PSMain() : SV_Target { return UNDEFINED_VAR; }";

        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(PixelRequest(badHlsl, PlatformTarget.OpenGL));

        result.IsFailure.Should().BeTrue();
        result.Error.FxcFormattedMessage.Should().Contain("(");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task CompileWithMacro_MacroVisibleToDxc()
    {
        // Without MY_MACRO the else-branch has a type error; with it, compilation succeeds.
        const string hlsl = """
            float4 VSMain(float4 pos : POSITION) : SV_Position {
            #ifdef MY_MACRO
                return pos;
            #else
                int shouldFail = pos;
                return float4(0,0,0,0);
            #endif
            }
            """;

        using var compiler = new DxcShaderCompiler();
        var request = new DxcCompileRequest
        {
            HlslSource = hlsl,
            SourceFileName = "test.fx",
            EntryPoint = "VSMain",
            Stage = ShaderStage.Vertex,
            Platform = PlatformTarget.OpenGL,
            Macros = [("MY_MACRO", null)],
        };
        var result = await compiler.CompileAsync(request);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.FxcFormattedMessage : "");
    }

    [Fact]
    public async Task CancellationBeforeInvocation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var compiler = new DxcShaderCompiler();
        var act = () => compiler.CompileAsync(VertexRequest(MinimalVs, PlatformTarget.OpenGL), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
