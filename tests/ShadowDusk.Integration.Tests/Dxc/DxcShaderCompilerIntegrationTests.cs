#nullable enable

using Shouldly;
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

    // Issue #229. SPIR-V header word 1 is the version: 0x00010000 = 1.0, 0x00010300 = 1.3.
    private const uint Spirv10 = 0x00010000;
    private const uint Spirv13 = 0x00010300;

    private const string WaveSumPs =
        "float4 PSMain(float2 uv : TEXCOORD0) : SV_Target { float v = WaveActiveSum(uv.x); return float4(v, v, v, 1); }";
    private const string QuadReadPs =
        "float4 PSMain(float2 uv : TEXCOORD0) : SV_Target { float v = QuadReadAcrossX(uv.x); return float4(v, v, v, 1); }";
    private const string WaveSumVs =
        "float4 VSMain(float4 pos : POSITION) : SV_Position { pos.x += WaveActiveSum(pos.y); return pos; }";
    private const string PlainPs =
        "float4 PSMain(float2 uv : TEXCOORD0) : SV_Target { return float4(uv, 0, 1); }";

    private static uint SpirvVersionWord(PlatformBlob blob)
        => BitConverter.ToUInt32(blob.Bytes.Span[4..8]);

    [Theory]
    [InlineData(WaveSumPs)]
    [InlineData(QuadReadPs)]
    [Trait("Platform", "Vulkan")]
    public async Task CompileWaveOrQuadPixel_Vulkan_SucceedsWithSpirv13(string hlsl)
    {
        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(PixelRequest(hlsl, PlatformTarget.Vulkan));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "");
        result.Value.Kind.ShouldBe(BlobKind.Spirv);
        SpirvVersionWord(result.Value).ShouldBe(Spirv13);
    }

    [Fact]
    [Trait("Platform", "Vulkan")]
    public async Task CompileWaveVertex_Vulkan_SucceedsWithSpirv13()
    {
        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(VertexRequest(WaveSumVs, PlatformTarget.Vulkan));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "");
        SpirvVersionWord(result.Value).ShouldBe(Spirv13);
    }

    [Fact]
    [Trait("Platform", "Vulkan")]
    public void CompileWaveSync_Vulkan_SucceedsWithSpirv13()
    {
        using var compiler = new DxcShaderCompiler();
        var result = compiler.Compile(PixelRequest(WaveSumPs, PlatformTarget.Vulkan));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "");
        SpirvVersionWord(result.Value).ShouldBe(Spirv13);
    }

    [Theory]
    [InlineData(ShaderStage.Vertex)]
    [InlineData(ShaderStage.Pixel)]
    [Trait("Platform", "Vulkan")]
    public async Task CompileNonWaveShader_Vulkan_StaysSpirv10_NoFallbackRecompile(ShaderStage stage)
    {
        using var compiler = new DxcShaderCompiler();
        var request = stage == ShaderStage.Vertex
            ? VertexRequest(MinimalVs, PlatformTarget.Vulkan)
            : PixelRequest(PlainPs, PlatformTarget.Vulkan);

        var result = await compiler.CompileAsync(request);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "");
        SpirvVersionWord(result.Value).ShouldBe(Spirv10);
    }

    [Fact]
    [Trait("Platform", "Vulkan")]
    public async Task CompileNonWaveShader_Vulkan_IsDeterministicAcrossCompiles()
    {
        using var compiler = new DxcShaderCompiler();
        var a = await compiler.CompileAsync(PixelRequest(PlainPs, PlatformTarget.Vulkan));
        var b = await compiler.CompileAsync(PixelRequest(PlainPs, PlatformTarget.Vulkan));

        a.Value.Bytes.ToArray().ShouldBe(b.Value.Bytes.ToArray());
    }

    [Fact]
    [Trait("Platform", "Vulkan")]
    public async Task CompileInvalidShader_Vulkan_ReturnsOriginalDiagnostic_NotAFallbackArtifact()
    {
        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(
            PixelRequest("float4 PSMain() : SV_Target { return undefinedSymbol; }", PlatformTarget.Vulkan));

        result.IsFailure.ShouldBeTrue();
        result.Error.Message.ShouldContain("undefinedSymbol", Case.Sensitive);
    }

    [Fact]
    [Trait("Platform", "Vulkan")]
    public async Task CompileWavePixel_Vulkan_StillFailsWhenWaveShaderIsOtherwiseInvalid()
    {
        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(PixelRequest(
            "float4 PSMain() : SV_Target { float v = WaveActiveSum(1.0); return float4(v, undefinedSymbol, 0, 1); }",
            PlatformTarget.Vulkan));

        result.IsFailure.ShouldBeTrue();
        result.Error.Message.ShouldContain("undefinedSymbol", Case.Sensitive);
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task CompileWavePixel_OpenGL_StillRejected_FallbackIsVulkanOnly()
    {
        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(PixelRequest(WaveSumPs, PlatformTarget.OpenGL));

        result.IsFailure.ShouldBeTrue();
        result.Error.Message.ShouldContain("Vulkan 1.1 is required", Case.Sensitive);
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task CompileMinimalVertex_OpenGL_ReturnsSpirvBlob()
    {
        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(VertexRequest(MinimalVs, PlatformTarget.OpenGL));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "");
        result.Value.Kind.ShouldBe(BlobKind.Spirv);
        result.Value.Bytes.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task CompileMinimalPixel_OpenGL_ReturnsSpirvBlob()
    {
        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(PixelRequest(MinimalPs, PlatformTarget.OpenGL));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "");
        result.Value.Kind.ShouldBe(BlobKind.Spirv);
        result.Value.Bytes.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    [Trait("Platform", "DirectX")]
    public async Task CompileMinimalVertex_DirectX_ReturnsDxilBlob()
    {
        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(VertexRequest(MinimalVs, PlatformTarget.DirectX));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "");
        // INTENTIONAL behavior change (bug-hunt 2026-07-27 N10): DXC's SM6 output IS
        // DXIL; the old Dxbc label was a mislabel this test pinned.
        result.Value.Kind.ShouldBe(BlobKind.Dxil);
        result.Value.Bytes.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    [Trait("Platform", "Vulkan")]
    public async Task CompileVulkanVertex_ReturnsSpirvBlob()
    {
        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(VertexRequest(MinimalVs, PlatformTarget.Vulkan));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "");
        result.Value.Kind.ShouldBe(BlobKind.Spirv);
        result.Value.Bytes.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task SyntaxError_ReturnsFailureWithFxcFormattedMessage()
    {
        // Missing closing brace — definite syntax error
        const string badHlsl = "float4 VSMain(float4 pos : POSITION) : SV_Position { return pos;";

        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(VertexRequest(badHlsl, PlatformTarget.OpenGL));

        result.IsFailure.ShouldBeTrue();
        result.Error.FxcFormattedMessage.ShouldContain("(", Case.Sensitive);
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task UndefinedVariable_ReturnsFailureWithLineCol()
    {
        const string badHlsl = "float4 PSMain() : SV_Target { return UNDEFINED_VAR; }";

        using var compiler = new DxcShaderCompiler();
        var result = await compiler.CompileAsync(PixelRequest(badHlsl, PlatformTarget.OpenGL));

        result.IsFailure.ShouldBeTrue();
        result.Error.FxcFormattedMessage.ShouldContain("(", Case.Sensitive);
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

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "");
    }

    [Fact]
    public async Task CancellationBeforeInvocation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var compiler = new DxcShaderCompiler();
        var act = () => compiler.CompileAsync(VertexRequest(MinimalVs, PlatformTarget.OpenGL), cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(act);
    }
}
