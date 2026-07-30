#nullable enable

using Shouldly;
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
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "compilation must succeed");
        return result.Value.Bytes;
    }

    [Fact]
    public async Task GetBindings_TexSamplerSpirV_ReturnsSuccess()
    {
        var spirvBlob = await CompileAsync(TexSamplerHlsl, PlatformTarget.OpenGL);

        var verifier = new SpvReflectionVerifier();
        var result = verifier.GetBindings(spirvBlob);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task GetBindings_TexSamplerSpirV_ReturnsNonNullBindingSlotMap()
    {
        var spirvBlob = await CompileAsync(TexSamplerHlsl, PlatformTarget.OpenGL);

        var verifier = new SpvReflectionVerifier();
        var result = verifier.GetBindings(spirvBlob);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task CompileTexSampler_BothDxilAndSpirV_BothSucceed()
    {
        // Compile the same source to both targets to verify the fixture is valid
        // on both paths.  Cross-target binding verification is deferred to Phase 6.
        var dxilBlob  = await CompileAsync(TexSamplerHlsl, PlatformTarget.DirectX);
        var spirvBlob = await CompileAsync(TexSamplerHlsl, PlatformTarget.OpenGL);

        dxilBlob.Length.ShouldBeGreaterThan(0, customMessage: "DXIL blob must be non-empty");
        spirvBlob.Length.ShouldBeGreaterThan(0, customMessage: "SPIR-V blob must be non-empty");
    }

    // -------------------------------------------------------------------------
    // SD0101 negative coverage (Phase 27, closing Phase 5 §7.3.3 / §9.2.5).
    //
    // The originally designed production mismatch path — SpvReflectionVerifier
    // comparing SPIRV-Cross-enumerated slots against DXIL slots and emitting
    // SD0101 — was never implemented: SpvReflectionVerifier is still a stub that
    // returns BindingSlotMap.Empty (the SPIRV-Cross C API wrapper exposes no
    // spvc_compiler_create_shader_resources). That design was superseded by the
    // pure-managed SpirvReflector (Phase 19), whose DXIL equivalence is enforced
    // by the SpirvVsDxilReflectionTests parity gate. The two tests below assert
    // the SD0101 error path that DOES exist in production (SpirvReflector) and
    // prove the parity gate's slot comparison cannot pass vacuously.
    // -------------------------------------------------------------------------

    [Fact]
    public void SpirvReflector_InvalidModule_ReturnsSD0101()
    {
        // Not a SPIR-V module (bad magic): the managed reflector must fail with
        // the SD0101 reflection error code, not throw and not return success.
        byte[] garbage = [0xEF, 0xBE, 0xAD, 0xDE, 0x00, 0x00, 0x00, 0x00];

        var result = new SpirvReflector().Reflect(garbage);

        result.IsFailure.ShouldBeTrue("garbage bytes are not a SPIR-V module");
        result.Error.Code.ShouldBe("SD0101");
        result.Error.Message.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task DivergentBindings_DxilVsSpirv_SlotMismatchIsDetectable()
    {
        // Negative control for the DXIL-vs-SPIR-V slot comparison
        // (SpirvVsDxilReflectionTests): compile two deliberately divergent layouts
        // and assert the divergence is VISIBLE in the reflected slots. Both
        // reflectors model slots as the per-class register RANK (SpirvReflectionParser
        // compacts DXC's flat auto-binding numbers per class to match the DXIL
        // oracle), so the divergence is constructed by RANK: on the SPIR-V side a
        // second texture/sampler pair occupies t0/s0, pushing Albedo to rank 1,
        // while the DXIL side binds Albedo at t0/s0. If either reflector flattened
        // slots (e.g. always 0), the corpus-wide parity gate would pass vacuously
        // and a real binding mismatch could ship silently.
        const string shiftedHlsl = """
            Texture2D    Padding       : register(t0);
            SamplerState PaddingSampler: register(s0);
            Texture2D    Albedo        : register(t1);
            SamplerState AlbedoSampler : register(s1);

            float4 PSMain(float2 uv : TEXCOORD0) : SV_Target
            {
                return Albedo.Sample(AlbedoSampler, uv) + Padding.Sample(PaddingSampler, uv);
            }
            """;

        ReadOnlyMemory<byte> dxilBlob  = await CompileAsync(TexSamplerHlsl, PlatformTarget.DirectX);
        ReadOnlyMemory<byte> spirvBlob = await CompileAsync(shiftedHlsl, PlatformTarget.OpenGL);

        var dxilResult = new DxilReflectionExtractor().Extract(dxilBlob);
        dxilResult.IsSuccess.ShouldBeTrue(dxilResult.IsFailure ? dxilResult.Error.Message : "DXIL reflection must succeed");

        var spirvResult = new SpirvReflector().Reflect(spirvBlob);
        spirvResult.IsSuccess.ShouldBeTrue(spirvResult.IsFailure ? spirvResult.Error.Message : "SPIR-V reflection must succeed");

        var dxilTexture  = dxilResult.Value.Textures.ShouldHaveSingleItem();
        var dxilSampler  = dxilResult.Value.Samplers.ShouldHaveSingleItem();
        var spirvTexture = spirvResult.Value.Textures
            .Where(t => t.Name == "Albedo").ShouldHaveSingleItem();
        var spirvSampler = spirvResult.Value.Samplers
            .Where(s => s.Name == "AlbedoSampler").ShouldHaveSingleItem();

        dxilTexture.BindSlot.ShouldBe(0, customMessage: "the DXIL side binds Albedo to t0");
        spirvTexture.BindSlot.ShouldBe(1, customMessage: "the SPIR-V side binds Albedo at rank t1");
        dxilSampler.BindSlot.ShouldBe(0, customMessage: "the DXIL side binds AlbedoSampler to s0");
        spirvSampler.BindSlot.ShouldBe(1, customMessage: "the SPIR-V side binds AlbedoSampler at rank s1");

        spirvTexture.BindSlot.ShouldNotBe(dxilTexture.BindSlot, customMessage: "a deliberately divergent layout must surface as differing reflected slots " +
                     "(this is exactly the comparison SpirvVsDxilReflectionTests enforces corpus-wide)");
    }
}
