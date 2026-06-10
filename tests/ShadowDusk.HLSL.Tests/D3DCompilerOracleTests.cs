#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// Pure unit tests for the d3dcompiler_47 oracle backend's request-policy guards.
/// The <c>ProfileOverride</c> refusal is checked BEFORE the Windows guard precisely so
/// it is unit-testable on every OS (no native d3dcompiler involved): the oracle must
/// never serve the SM1–3 (FNA) path, or output would silently depend on which DXBC
/// backend a host picked.
/// </summary>
public sealed class D3DCompilerOracleTests
{
    [Theory]
    [InlineData("ps_2_0")]
    [InlineData("vs_3_0")]
    public async Task ProfileOverride_IsRefusedWithSd0210_OnEveryOs(string profile)
    {
        var compiler = new D3DCompilerShaderCompiler();
        var request = new D3DCompileRequest
        {
            HlslSource      = "float4 PS() : COLOR { return float4(1,1,1,1); }",
            SourceFileName  = "oracle.fx",
            EntryPoint      = "PS",
            Stage           = ShaderStage.Pixel,
            ProfileOverride = profile,
        };

        var result = await compiler.CompileAsync(request);

        result.IsFailure.Should().BeTrue(
            because: "the oracle must refuse ProfileOverride loudly, never silently ignore it");
        result.Error.Code.Should().Be("SD0210");
        result.Error.Message.Should().Contain(profile)
            .And.Contain("vkd3d", because: "the diagnostic must point at the backend that owns SM1–3");
    }
}
