#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Integration.Tests.Tests;
using Xunit;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// The language version as the real DXC sees it (Phase 70, issue #287). DXC 1.9 defaults to HLSL
/// 2021, where a vector condition in <c>?:</c> and vector <c>&amp;&amp;</c> / <c>||</c> are errors.
/// FX shaders use them and ShadowDusk has always compiled them, so moving the pin must not stop
/// them compiling on any DXC target.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DxcLanguageVersionCompileTests
{
    private const string Technique = "technique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }\n";

    private static string Effect(string body) =>
        "float4 MainPS(float4 pos : SV_Position, float4 c : COLOR0) : SV_Target0 {\n" +
        "float3 a = c.rgb;\n" + body + "\nreturn float4(r, 1);\n}\n" + Technique;

    private static async Task<Result<CompiledShader, ShaderError[]>> CompileAsync(string source, PlatformTarget target)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        return await new EffectCompiler().CompileAsync(
            source, new CompilerOptions { Target = target, SourceFileName = "language.fx" }, cts.Token);
    }

    private static string Describe(Result<CompiledShader, ShaderError[]> result) =>
        result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "compiled";

    [DxcTheory]
    [InlineData(PlatformTarget.OpenGL, "float3 r = (a > 0.5) ? a : 1 - a;")]
    [InlineData(PlatformTarget.Vulkan, "float3 r = (a > 0.5) ? a : 1 - a;")]
    [InlineData(PlatformTarget.DirectX12, "float3 r = (a > 0.5) ? a : 1 - a;")]
    [InlineData(PlatformTarget.OpenGL, "float3 r = (a > 0.5) && (a < 0.9);")]
    [InlineData(PlatformTarget.Vulkan, "float3 r = (a > 0.5) && (a < 0.9);")]
    [InlineData(PlatformTarget.DirectX12, "float3 r = (a > 0.5) && (a < 0.9);")]
    [InlineData(PlatformTarget.OpenGL, "float3 r = (a > 0.5) || (a < 0.1);")]
    [InlineData(PlatformTarget.Vulkan, "float3 r = (a > 0.5) || (a < 0.1);")]
    [InlineData(PlatformTarget.DirectX12, "float3 r = (a > 0.5) || (a < 0.1);")]
    public async Task AVectorConditionOr2021Refuses_StillCompiles(PlatformTarget target, string body)
    {
        var result = await CompileAsync(Effect(body), target);

        result.IsSuccess.ShouldBeTrue(Describe(result));
    }

    [DxcTheory]
    [InlineData(PlatformTarget.DirectX12, 2021)]
    [InlineData(PlatformTarget.Vulkan, 2021)]
    [InlineData(PlatformTarget.OpenGL, 2018)]
    public async Task ACompile_SeesItsTargetsLanguageVersion(PlatformTarget target, int version)
    {
        string source = $"#if __HLSL_VERSION != {version}\n#error wrong language version\n#endif\n" +
                        Effect("float3 r = a;");

        var result = await CompileAsync(source, target);

        result.IsSuccess.ShouldBeTrue(Describe(result));
    }

    [DxcFact]
    public async Task Vulkan_ABranchOnTheVersionThatFailsAt2021_ReportsThatFailure()
    {
        // The pairing read the 2021 branch; compiling the 2018 one instead would ship code it
        // never saw, so the 2021 error stands.
        string source = Effect(
            "#if __HLSL_VERSION >= 2021\nfloat3 r = (a > 0.5) ? a : 1 - a;\n#else\nfloat3 r = a;\n#endif");

        var result = await CompileAsync(source, PlatformTarget.Vulkan);

        result.IsFailure.ShouldBeTrue("the source must not fall back to the branch the pairing did not read");
        result.Error[0].Message.ShouldContain("ternary", Case.Insensitive);
    }

    [DxcFact]
    public async Task DirectX12_ABranchOnTheVersionThatFailsAt2021_FallsBack()
    {
        // DirectX 12 has no pairing view, so nothing depends on the branch 2021 would have taken.
        string source = Effect(
            "#if __HLSL_VERSION >= 2021\nfloat3 r = (a > 0.5) ? a : 1 - a;\n#else\nfloat3 r = a;\n#endif");

        var result = await CompileAsync(source, PlatformTarget.DirectX12);

        result.IsSuccess.ShouldBeTrue(Describe(result));
    }

    [DxcTheory]
    [InlineData(PlatformTarget.Vulkan)]
    [InlineData(PlatformTarget.DirectX12)]
    public async Task AGenuineError_IsStillReported(PlatformTarget target)
    {
        var result = await CompileAsync(Effect("float3 r = notDeclared;"), target);

        result.IsFailure.ShouldBeTrue();
        result.Error[0].Message.ShouldContain("notDeclared", Case.Sensitive);
    }
}
