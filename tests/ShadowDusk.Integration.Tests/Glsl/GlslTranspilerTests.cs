#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL.Dxc;
using System.Runtime.InteropServices;
using Xunit;

namespace ShadowDusk.Integration.Tests.Glsl;

[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class GlslTranspilerTests
{
    private const string MinimalVs = "float4 VSMain(float4 pos : POSITION) : SV_Position { return pos; }";
    private const string MinimalPs = "float4 PSMain() : SV_Target { return float4(1,0,0,1); }";

    private const string TexturedPs = """
        Texture2D Texture0;
        SamplerState Sampler0;
        struct VSOutput { float4 Position : SV_Position; float2 UV : TEXCOORD0; };
        float4 PSMain(VSOutput input) : SV_Target {
            return Texture0.Sample(Sampler0, input.UV);
        }
        """;

    private static async Task<ReadOnlyMemory<byte>> CompileToSpirvAsync(
        string hlsl, ShaderStage stage, string entry)
    {
        using var compiler = new DxcShaderCompiler();
        var request = new DxcCompileRequest
        {
            HlslSource = hlsl,
            SourceFileName = "test.fx",
            EntryPoint = entry,
            Stage = stage,
            Platform = PlatformTarget.OpenGL,
        };
        var result = await compiler.CompileAsync(request);
        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.FxcFormattedMessage : "");
        result.Value.Kind.Should().Be(BlobKind.Spirv);
        return result.Value.Bytes;
    }

    [Fact]
    public async Task Transpile_MinimalVertex_OutputContainsVoidMain()
    {
        var spirvBytes = await CompileToSpirvAsync(MinimalVs, ShaderStage.Vertex, "VSMain");
        var transpiler = new SpirvCrossGlslTranspiler();

        var result = transpiler.Transpile(spirvBytes);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "");
        result.Value.Text.Should().Contain("void main(");
    }

    [Fact]
    public async Task Transpile_MinimalVertex_OutputStartsWithVersion140()
    {
        var spirvBytes = await CompileToSpirvAsync(MinimalVs, ShaderStage.Vertex, "VSMain");
        var transpiler = new SpirvCrossGlslTranspiler();

        var result = transpiler.Transpile(spirvBytes);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "");
        result.Value.Text.Should().StartWith("#version 140");
    }

    [Fact]
    public async Task Transpile_MinimalPixel_OutputContainsVoidMain()
    {
        var spirvBytes = await CompileToSpirvAsync(MinimalPs, ShaderStage.Pixel, "PSMain");
        var transpiler = new SpirvCrossGlslTranspiler();

        var result = transpiler.Transpile(spirvBytes);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "");
        result.Value.Text.Should().Contain("void main(");
    }

    [Fact]
    public async Task Transpile_TexturedPixel_OutputContainsSampler2D()
    {
        var spirvBytes = await CompileToSpirvAsync(TexturedPs, ShaderStage.Pixel, "PSMain");
        var transpiler = new SpirvCrossGlslTranspiler();

        var result = transpiler.Transpile(spirvBytes);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "");
        result.Value.Text.Should().Contain("sampler2D");
    }

    [Fact]
    public async Task Transpile_PassthroughVertex_NoStaticYFlip_DepthFixupKept()
    {
        // Phase 43 F3: the OpenGL DXC flags omit -fvk-invert-y (see DxcFlagBuilder)
        // AND SPIRV-Cross's FlipVertexY option is now OFF — the Y-flip is the runtime
        // posFixup uniform's job (injected later by MonoGameGlslRewriter, set per
        // draw by MonoGame: +1 backbuffer / -1 render target). A baked negation here
        // would DOUBLE-flip the render-target case and stay wrong on the backbuffer.
        // The depth-convention fixup (FixupDepthConvention) must remain.
        string fxPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders", "passthrough_vs.fx");
        File.Exists(fxPath).Should().BeTrue($"fixture must exist at {fxPath}");
        string hlsl = await File.ReadAllTextAsync(fxPath);

        var spirvBytes = await CompileToSpirvAsync(hlsl, ShaderStage.Vertex, "VSMain");
        var transpiler = new SpirvCrossGlslTranspiler();

        var result = transpiler.Transpile(spirvBytes);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "");
        result.Value.Text.Should().NotContain("gl_Position.y = -gl_Position.y",
            because: "FlipVertexY is off — the Y-flip belongs to the runtime posFixup contract");
        result.Value.Text.Should().Contain("gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;",
            because: "FixupDepthConvention (DX [0,1] depth to GL [-1,1]) must stay on");
    }

    [Fact]
    public void Transpile_InvalidSpirv_ReturnsShaderError()
    {
        var transpiler = new SpirvCrossGlslTranspiler();
        ReadOnlySpan<uint> garbage = new uint[] { 0xDEADBEEF, 0, 0, 0 };

        var result = transpiler.Transpile(garbage);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Transpile_EmptySpirv_ReturnsShaderError()
    {
        var transpiler = new SpirvCrossGlslTranspiler();

        var result = transpiler.Transpile(ReadOnlySpan<uint>.Empty);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().NotBeNullOrEmpty();
    }
}
