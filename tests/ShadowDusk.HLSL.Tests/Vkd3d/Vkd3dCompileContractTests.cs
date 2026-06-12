#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Vkd3d;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Vkd3d;

/// <summary>
/// PURE unit tests (no disk, no process, no native) for
/// <see cref="Vkd3dCompileContract"/> — the request→ABI argument mapping and error
/// mapping SHARED by the desktop P/Invoke backend (<c>Vkd3dShaderCompiler</c>) and
/// the browser/WASM backend (<c>WasmVkd3dShaderCompiler</c>, Phase 4.1). Pinning this
/// contract here is what guarantees the two hosts ask vkd3d the identical question.
/// </summary>
public sealed class Vkd3dCompileContractTests
{
    private static D3DCompileRequest Request(ShaderStage stage, string? profileOverride = null) => new()
    {
        HlslSource      = "float4 PS() : SV_TARGET { return 0; }",
        SourceFileName  = "test.fx",
        EntryPoint      = "PS",
        Stage           = stage,
        ProfileOverride = profileOverride,
    };

    // -------------------------------------------------------------------------
    // Profile resolution — SM5 stage defaults, override wins (the FNA path)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(ShaderStage.Vertex, "vs_5_0")]
    [InlineData(ShaderStage.Pixel,  "ps_5_0")]
    public void ResolveProfile_DefaultsToSm5ForStage(ShaderStage stage, string expected)
    {
        Vkd3dCompileContract.ResolveProfile(Request(stage)).Should().Be(expected,
            because: "the MonoGame DX11 path compiles at SM5 when no override is given — " +
                     "the desktop Vkd3dShaderCompiler default the WASM backend must mirror");
    }

    [Theory]
    [InlineData("ps_2_0")]
    [InlineData("vs_3_0")]
    [InlineData("ps_2_b")]
    public void ResolveProfile_OverrideWins(string profileOverride)
    {
        Vkd3dCompileContract.ResolveProfile(Request(ShaderStage.Pixel, profileOverride))
            .Should().Be(profileOverride, because: "ProfileOverride (the FNA SM ≤ 3 path) is verbatim");
    }

    [Fact]
    public void ResolveProfile_UnsupportedStage_Throws()
    {
        var act = () => Vkd3dCompileContract.ResolveProfile(Request((ShaderStage)99));

        act.Should().Throw<ArgumentOutOfRangeException>(
            because: "an unmapped stage is a programming error, not a compile diagnostic");
    }

    // -------------------------------------------------------------------------
    // SM routing — SM ≤ 3 → D3D_BYTECODE (4), else DXBC_TPF (5)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("ps_1_1")]
    [InlineData("ps_2_0")]
    [InlineData("ps_2_b")]
    [InlineData("vs_3_0")]
    public void Sm3OrBelow_RoutesToD3dBytecode(string profile)
    {
        Vkd3dCompileContract.IsSm3OrBelow(profile).Should().BeTrue();
        Vkd3dCompileContract.ResolveTargetType(profile)
            .Should().Be(Vkd3dCompileContract.TargetTypeD3dBytecode);
        Vkd3dCompileContract.ResolveBlobKind(profile).Should().Be(BlobKind.D3dBytecode);
    }

    [Theory]
    [InlineData("vs_4_0")]
    [InlineData("ps_5_0")]
    [InlineData("vs_5_0")]
    public void Sm4AndUp_RoutesToDxbcTpf(string profile)
    {
        Vkd3dCompileContract.IsSm3OrBelow(profile).Should().BeFalse();
        Vkd3dCompileContract.ResolveTargetType(profile)
            .Should().Be(Vkd3dCompileContract.TargetTypeDxbcTpf);
        Vkd3dCompileContract.ResolveBlobKind(profile).Should().Be(BlobKind.Dxbc);
    }

    [Theory]
    [InlineData("garbage")]   // no underscore
    [InlineData("ps_")]       // nothing after the underscore
    [InlineData("ps_x_0")]    // non-digit SM major
    public void UnparseableProfile_FallsThroughToDxbcTpf_SoVkd3dRejectsItLoudly(string profile)
    {
        // Constraint 5: an unparseable profile must reach vkd3d (DXBC_TPF arm) so the
        // consumer gets vkd3d's own diagnostic — never a silent reroute.
        Vkd3dCompileContract.IsSm3OrBelow(profile).Should().BeFalse();
        Vkd3dCompileContract.ResolveTargetType(profile)
            .Should().Be(Vkd3dCompileContract.TargetTypeDxbcTpf);
    }

    [Fact]
    public void TargetTypeConstants_MatchTheVkd3dAbiAndTheWasmWrapperContract()
    {
        // 4/5 are pinned by BOTH the vkd3d 1.17 enum (Vkd3dNative.cs, verified against
        // vkd3d_shader.h) and the Phase 4.1 sdw_vkd3d_compile wrapper contract. If this
        // ever fails, one side of the [JSImport]/P-Invoke split has drifted.
        Vkd3dCompileContract.TargetTypeD3dBytecode.Should().Be((int)Vkd3dTargetType.D3dBytecode);
        Vkd3dCompileContract.TargetTypeDxbcTpf.Should().Be((int)Vkd3dTargetType.DxbcTpf);
        Vkd3dCompileContract.TargetTypeD3dBytecode.Should().Be(4);
        Vkd3dCompileContract.TargetTypeDxbcTpf.Should().Be(5);
    }

    // -------------------------------------------------------------------------
    // Error mapping — verbatim diagnostics first, SD0212 fallback
    // -------------------------------------------------------------------------

    [Fact]
    public void MapCompileFailure_ParsesVerbatimDiagnosticLine()
    {
        const string messages = "test.fx(12,5): error E5005: variable 'foo' is undefined\n";

        ShaderError error = Vkd3dCompileContract.MapCompileFailure(messages, "test.fx", "fallback");

        error.File.Should().Be("test.fx");
        error.Line.Should().Be(12);
        error.Column.Should().Be(5);
        error.Code.Should().Be("E5005");
        error.Message.Should().Be("variable 'foo' is undefined",
            because: "constraint 5: the message is surfaced exactly as the compiler emitted it");
    }

    [Fact]
    public void MapCompileFailure_EmptyMessages_FallsBackToSd0212WithCallerText()
    {
        ShaderError error = Vkd3dCompileContract.MapCompileFailure(
            "", "test.fx", "vkd3d-shader DXBC compilation failed (rc=-4) with no diagnostics");

        error.Code.Should().Be("SD0212");
        error.Message.Should().Be("vkd3d-shader DXBC compilation failed (rc=-4) with no diagnostics");
        error.RawDiagnostics.Should().BeNull();
    }

    [Fact]
    public void MapCompileFailure_UnstructuredMessages_CarriesRawTextNotSwallowed()
    {
        const string messages = "some completely unstructured vkd3d output";

        ShaderError error = Vkd3dCompileContract.MapCompileFailure(messages, "test.fx", "fallback");

        // The reformatter wraps unmatched text as a raw-diagnostics error — the text
        // must remain reachable (constraint 5), whatever the wrapper shape.
        error.RawDiagnostics.Should().Contain(messages);
    }
}
