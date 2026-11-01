#nullable enable

using System.Text.RegularExpressions;
using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Preprocessing;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Dxc;

/// <summary>
/// Issue #442: the values <see cref="DxcSpirvPredefinedMacros"/> gives the Vulkan pairing's view are
/// DXC's own. Each is re-measured here against the loaded DXC (<c>-P</c>, ShadowDusk's exact Vulkan
/// argument set per stage), on every host the suite runs on, so a DXC change that moves one (the
/// DXC bump, issue #287) fails here instead of silently pairing a branch DXC does not compile.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DxcSpirvPredefinedMacrosTests
{
    private static readonly DxcShaderCompiler Compiler = new();

    // Names DXC might define that are NOT seeded, and why: the DXC version stamp is host-dependent.
    private static readonly string[] DxcVersionNames =
        ["__DXC_VERSION_MAJOR", "__DXC_VERSION_MINOR", "__DXC_VERSION_RELEASE", "__DXC_VERSION_COMMITS"];

    /// <summary>What DXC's preprocessor gives each name for the Vulkan <paramref name="stage"/> compile ("" when undefined).</summary>
    private static Dictionary<string, string> Measure(ShaderStage stage, IEnumerable<string> names)
    {
        string[] list = names.ToArray();
        // Each line is keyed by the name's index: the name itself would expand.
        string probe = string.Concat(list.Select((n, i) => $"#ifdef {n}\nSDPROBE {i} = {n} ;\n#else\nSDPROBE {i} = ;\n#endif\n"));
        IReadOnlyList<string> arguments = DxcSpirvEntryRedeclarations.PreprocessArguments(
            DxcFlagBuilder.Build(PlatformTarget.Vulkan, stage, "main", [], new DxcCompileOptions()));
        DxcSpirvEntryRedeclarations.PreprocessedText text = Compiler.PreprocessOnly(probe, arguments, includeHandler: null);
        text.Succeeded.ShouldBeTrue("DXC must preprocess the probe");

        var measured = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text.Text, @"SDPROBE\s+(\d+)\s*=\s*([^;]*?)\s*;"))
            measured[list[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]] = m.Groups[2].Value;
        measured.Keys.ShouldBe(list, ignoreOrder: true, "every probed name must come back");
        return measured;
    }

    [Theory]
    [InlineData(ShaderStage.Vertex)]
    [InlineData(ShaderStage.Pixel)]
    public void EverySeededValue_IsWhatTheLoadedDxcDefines(ShaderStage stage)
    {
        IReadOnlyList<(string Name, string Value)> seeded = DxcSpirvPredefinedMacros.Vulkan(stage);
        Dictionary<string, string> measured = Measure(stage, seeded.Select(m => m.Name));

        foreach ((string name, string value) in seeded)
            measured[name].ShouldBe(value, $"{stage}: DXC's {name} moved; re-measure DxcSpirvPredefinedMacros (issue #442)");
    }

    [Fact]
    public void TheStageMacro_DiffersByStage_AndNamesTheStage()
    {
        Measure(ShaderStage.Vertex, ["__SHADER_TARGET_STAGE", "__SHADER_STAGE_VERTEX"]).Values.Distinct().ShouldHaveSingleItem();
        Measure(ShaderStage.Pixel, ["__SHADER_TARGET_STAGE", "__SHADER_STAGE_PIXEL"]).Values.Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public void TheDxcVersionStamp_IsNotSeeded_BecauseItIsHostDependent()
    {
        // Measured 2026-10-31: the same pinned DXC prints 1.7.2212.40 on Windows and 1.7.0.3759 on
        // Linux (Vortice.Dxc 3.3.4's linux-x64 build, "dxc(private) 1.7.0.3759"). A seed would be
        // wrong on some host, so a conditional on it keeps the view untrusted (whole-file pairing).
        Dictionary<string, string> measured = Measure(ShaderStage.Pixel, DxcVersionNames);
        measured.Values.ShouldAllBe(v => v.Length > 0, "DXC defines its version stamp");
        foreach (string name in DxcVersionNames)
            DxcSpirvPredefinedMacros.Vulkan(ShaderStage.Pixel).ShouldNotContain(m => m.Name == name);

        if (OperatingSystem.IsWindows())
        {
            string version = string.Join(".", DxcVersionNames.Select(n => measured[n]));
            DxcNativeIdentity.WindowsCompilerVersion.ShouldStartWith(version + " ",
                customMessage: "the Windows DXC's version stamp is its pinned file version");
        }
    }

    [Fact]
    public void TheSeeds_WereMeasuredOnThePinnedDxc()
    {
        // A DXC bump (issue #287) changes the pin; this fails until the seeds are re-measured on
        // the new DXC (every host: EverySeededValue_IsWhatTheLoadedDxcDefines) and this line moves.
        DxcSpirvPredefinedMacros.MeasuredOnDxc.ShouldBe(DxcNativeIdentity.WindowsCompilerVersion,
            "DxcSpirvPredefinedMacros was measured on another DXC: re-measure it (issue #442) and update MeasuredOnDxc");
    }
}
