#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// A <c>.fx</c> wave/quad intrinsic fails loudly with a registered code on every target that
/// cannot hold it (<c>SD0624</c> on OpenGL, DirectX 11 and FNA, <c>SD0218</c> on Vulkan), at the
/// call's own <c>.fx</c> line and column behind preprocessor drift, and still compiles on
/// DirectX 12. Before this, OpenGL surfaced DXC's "Vulkan 1.1 is required" and DX11/FNA surfaced
/// vkd3d's "Function is not defined", both under unregistered codes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WaveIntrinsicTargetRejectionTests
{
    // The two comment lines are planted drift: the compilers count the flattened,
    // macro-prefixed text, so their raw coordinates differ from the .fx file's.
    private static string Source(string vsProfile, string psProfile, string call) => $$"""
        // padding line one
        // padding line two
        float4 Tint;

        struct VOut { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

        VOut VS(float4 pos : POSITION0, float2 uv : TEXCOORD0)
        {
            VOut o;
            o.Pos = pos;
            o.UV = uv;
            return o;
        }

        float4 PS(VOut i) : SV_Target0
        {
            float v = {{call}};
            return float4(v, v, v, 1) * Tint;
        }

        technique T
        {
            pass P { VertexShader = compile {{vsProfile}} VS(); PixelShader = compile {{psProfile}} PS(); }
        }
        """;

    private const int CallLine = 17;
    private const int CallColumn = 15;

    private static async Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        PlatformTarget target, string vs, string ps, string call)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        return await new EffectCompiler().CompileAsync(Source(vs, ps, call), new CompilerOptions
        {
            Target = target,
            SourceFileName = "Wave.fx",
        }, cts.Token);
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL, "vs_4_0", "ps_4_0", "WaveActiveSum(i.UV.x)", "WaveActiveSum")]
    [InlineData(PlatformTarget.OpenGL, "vs_4_0", "ps_4_0", "QuadReadAcrossX(i.UV.x)", "QuadReadAcrossX")]
    [InlineData(PlatformTarget.DirectX, "vs_4_0", "ps_4_0", "WaveActiveSum(i.UV.x)", "WaveActiveSum")]
    [InlineData(PlatformTarget.DirectX, "vs_4_0", "ps_4_0", "QuadReadAcrossX(i.UV.x)", "QuadReadAcrossX")]
    [InlineData(PlatformTarget.Fna, "vs_3_0", "ps_3_0", "WaveActiveSum(i.UV.x)", "WaveActiveSum")]
    [InlineData(PlatformTarget.Fna, "vs_3_0", "ps_3_0", "QuadReadAcrossX(i.UV.x)", "QuadReadAcrossX")]
    public async Task Compile_WaveOrQuadIntrinsic_OnCappedTarget_RejectedWithSD0624_AtTheCall(
        PlatformTarget target, string vs, string ps, string call, string intrinsic)
    {
        var result = await CompileAsync(target, vs, ps, call);

        result.IsFailure.ShouldBeTrue($"{intrinsic} must never produce a {target} build");
        var error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0624");
        error.Message.ShouldContain($"'{intrinsic}'", Case.Sensitive);
        error.Message.ShouldContain($"on the {target} target", Case.Sensitive);
        error.File.ShouldEndWith("Wave.fx", Case.Sensitive);
        error.Line.ShouldBe(CallLine);
        error.Column.ShouldBe(CallColumn);
    }

    // The opt-in Windows-only d3dcompiler_47 backend words the failure differently from vkd3d, so
    // it needs its own measured proof. Skipped off Windows with a reason; on Windows a missing
    // d3dcompiler fails the code assertion rather than passing.
    [WindowsFact]
    public async Task Compile_WaveIntrinsic_DirectX_D3DCompilerBackend_RejectedWithSD0624_AtTheCall()
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);

        var result = await new EffectCompiler().CompileAsync(
            Source("vs_4_0", "ps_4_0", "WaveActiveSum(i.UV.x)"), new CompilerOptions
            {
                Target = PlatformTarget.DirectX,
                DxbcBackend = DxbcBackend.D3DCompiler,
                SourceFileName = "Wave.fx",
            }, cts.Token);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0624", error.Message);
        error.Message.ShouldContain("'WaveActiveSum'", Case.Sensitive);
        error.Message.ShouldContain("undeclared identifier 'WaveActiveSum'", Case.Sensitive);
        error.File.ShouldEndWith("Wave.fx", Case.Sensitive);
        error.Line.ShouldBe(CallLine);
        error.Column.ShouldBe(CallColumn);
    }

    [Fact]
    public async Task Compile_WaveIntrinsic_Vulkan_RejectedWithSD0218_AtTheCall()
    {
        var result = await CompileAsync(PlatformTarget.Vulkan, "vs_6_0", "ps_6_0", "WaveActiveSum(i.UV.x)");

        var error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0218");
        error.Message.ShouldContain("'WaveActiveSum'", Case.Sensitive);
        error.Line.ShouldBe(CallLine);
        error.Column.ShouldBe(CallColumn);
    }

    [Fact]
    public async Task Compile_WaveIntrinsic_DirectX12_StillCompiles()
    {
        var result = await CompileAsync(PlatformTarget.DirectX12, "vs_6_0", "ps_6_0", "WaveActiveSum(i.UV.x)");

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
    }

    // The relabel keys on the compiler's own wording, so a user function that merely shares an
    // intrinsic's name on an SM5 target (and compiles) is not rejected.
    [Fact]
    public async Task Compile_UserFunctionNamedLikeAnIntrinsic_OnDirectX_StillCompiles()
    {
        const string source = """
            float WaveActiveSum(float x) { return x * 2; }
            float4 PS(float2 uv : TEXCOORD0) : SV_Target0 { float v = WaveActiveSum(uv.x); return float4(v, v, v, 1); }
            technique T { pass P { PixelShader = compile ps_4_0 PS(); } }
            """;
        using var cts = new CancellationTokenSource(TestBudget.Compile);

        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.DirectX,
            SourceFileName = "Shadow.fx",
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
    }
}
