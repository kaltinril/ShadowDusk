#nullable enable

using Shouldly;
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

        result.IsFailure.ShouldBeTrue("the oracle must refuse ProfileOverride loudly, never silently ignore it");
        result.Error.Code.ShouldBe("SD0210");
        result.Error.Message.ShouldContain(profile, Case.Sensitive);
        result.Error.Message.ShouldContain(
            "vkd3d", Case.Sensitive, "the diagnostic must point at the backend that owns SM1–3");
    }
}
