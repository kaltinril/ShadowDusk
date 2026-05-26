#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Reflection;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class SpvBindingVerificationTests
{
    // Inlined from tests/fixtures/shaders/reflection/tex_sampler.hlsl
    private const string TexSamplerHlsl = """
        Texture2D    Albedo        : register(t0);
        SamplerState AlbedoSampler : register(s0);

        float4 PSMain(float2 uv : TEXCOORD0) : SV_Target
        {
            return Albedo.Sample(AlbedoSampler, uv);
        }
        """;

    private static async Task<ReadOnlyMemory<byte>> CompileAsync(string hlsl, PlatformTarget platform)
    {
        using var compiler = new DxcShaderCompiler();
        var request = new DxcCompileRequest
        {
            HlslSource     = hlsl,
            SourceFileName = "tex_sampler.hlsl",
            EntryPoint     = "PSMain",
            Stage          = ShaderStage.Pixel,
            Platform       = platform,
        };
        var result = await compiler.CompileAsync(request);
        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure ? result.Error.FxcFormattedMessage : "compilation must succeed");
        return result.Value.Bytes;
    }

    [Fact]
    public async Task GetBindings_TexSamplerSpirV_ReturnsSuccess()
    {
        var spirvBlob = await CompileAsync(TexSamplerHlsl, PlatformTarget.OpenGL);

        var verifier = new SpvReflectionVerifier();
        var result = verifier.GetBindings(spirvBlob);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetBindings_TexSamplerSpirV_ReturnsNonNullBindingSlotMap()
    {
        var spirvBlob = await CompileAsync(TexSamplerHlsl, PlatformTarget.OpenGL);

        var verifier = new SpvReflectionVerifier();
        var result = verifier.GetBindings(spirvBlob);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task CompileTexSampler_BothDxilAndSpirV_BothSucceed()
    {
        // Compile the same source to both targets to verify the fixture is valid
        // on both paths.  Cross-target binding verification is deferred to Phase 6.
        var dxilBlob  = await CompileAsync(TexSamplerHlsl, PlatformTarget.DirectX);
        var spirvBlob = await CompileAsync(TexSamplerHlsl, PlatformTarget.OpenGL);

        dxilBlob.Length.Should().BeGreaterThan(0,  because: "DXIL blob must be non-empty");
        spirvBlob.Length.Should().BeGreaterThan(0, because: "SPIR-V blob must be non-empty");
    }

    // TODO Phase 6: add mismatch assertion once SPIRV-Cross P/Invoke is in place
}
