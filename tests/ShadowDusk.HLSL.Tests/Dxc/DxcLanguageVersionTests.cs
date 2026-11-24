#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Dxc;

/// <summary>
/// The HLSL language version each DXC compile runs at (Phase 70, issue #287): what the target's
/// reference compiler uses, with 2018 as the fallback for a source 2021 refuses.
/// </summary>
public sealed class DxcLanguageVersionTests
{
    private static readonly ShaderError DxcError = new("a.fx", 1, 1, "X0000", "any DXC diagnostic");

    private static DxcCompileRequest Request(PlatformTarget platform, string source = "float4 PS() : SV_Target { return 0; }") => new()
    {
        HlslSource = source,
        SourceFileName = "a.fx",
        EntryPoint = "PS",
        Stage = ShaderStage.Pixel,
        Platform = platform,
    };

    [Theory]
    [InlineData(PlatformTarget.DirectX12, "2021")]
    [InlineData(PlatformTarget.Vulkan, "2021")]
    [InlineData(PlatformTarget.OpenGL, "2018")]
    [InlineData(PlatformTarget.DirectX, "2018")]
    public void EachTarget_StatesItsLanguageVersion(PlatformTarget platform, string expected)
    {
        DxcLanguageVersion.For(platform).ShouldBe(expected);

        IReadOnlyList<string> args = DxcFlagBuilder.Build(platform, ShaderStage.Pixel, "PS", []);
        args[args.ToList().IndexOf("-HV") + 1].ShouldBe(expected);
    }

    [Fact]
    public void AnExplicitVersion_OverridesTheTargets()
    {
        IReadOnlyList<string> args = DxcFlagBuilder.Build(
            PlatformTarget.Vulkan, ShaderStage.Pixel, "PS", [], languageVersion: DxcLanguageVersion.Legacy);

        args[args.ToList().IndexOf("-HV") + 1].ShouldBe("2018");
    }

    [Theory]
    [InlineData(null, "2018")]
    [InlineData("2021", "2021")]
    public void Preprocess_RunsAtTheRequestedVersion(string? requested, string expected)
    {
        IReadOnlyList<string> args = DxcFlagBuilder.BuildPreprocess([], requested);

        args[args.ToList().IndexOf("-HV") + 1].ShouldBe(expected);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX12, true)]
    [InlineData(PlatformTarget.Vulkan, true)]
    [InlineData(PlatformTarget.OpenGL, false)]
    [InlineData(PlatformTarget.DirectX, false)]
    public void ADxcDiagnostic_RetriesOnlyOnTheTargetsThatStartAt2021(PlatformTarget platform, bool expected)
    {
        DxcLanguageVersion.MayRetryAsLegacy(Request(platform), DxcError).ShouldBe(expected);
    }

    [Theory]
    [InlineData("SD0241")]
    [InlineData("SD0236")]
    [InlineData("SD0210")]
    public void AShadowDuskRefusal_IsNeverRetried(string code)
    {
        DxcLanguageVersion.MayRetryAsLegacy(Request(PlatformTarget.DirectX12), DxcError with { Code = code })
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData("#if __HLSL_VERSION >= 2021\nint a;\n#else\nint b;\n#endif\n")]
    [InlineData("#if __HLSL_VERSION < 2021\nint a;\n#endif\n")]
    public void Vulkan_ASourceWhoseViewDependsOnTheVersion_KeepsIts2021Error(string source)
    {
        DxcLanguageVersion.MayRetryAsLegacy(Request(PlatformTarget.Vulkan, source), DxcError).ShouldBeFalse();
    }

    [Theory]
    [InlineData("// a comment about include paths, why??\nint a;\n")]
    [InlineData("#if __HLSL_VERSION >= 2016\nint a;\n#endif\n")]
    public void Vulkan_ASourceWhoseViewIsTheSameAtBothVersions_IsRetried(string source)
    {
        DxcLanguageVersion.MayRetryAsLegacy(Request(PlatformTarget.Vulkan, source), DxcError).ShouldBeTrue();
    }

    [Fact]
    public void DirectX12_HasNoPairingView_SoAVersionBranchIsStillRetried()
    {
        const string source = "#if __HLSL_VERSION >= 2021\nint a;\n#else\nint b;\n#endif\n";

        DxcLanguageVersion.MayRetryAsLegacy(Request(PlatformTarget.DirectX12, source), DxcError).ShouldBeTrue();
    }
}
