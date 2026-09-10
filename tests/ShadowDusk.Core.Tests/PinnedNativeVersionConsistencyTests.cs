#nullable enable

using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Guards every place that NAMES the pinned vkd3d-shader version against the place that
/// actually pins it (<c>tools/restore.sh</c>'s release-tag URL).
///
/// <para>The 1.17 → 2.1 bump repinned the restore scripts and then left the version behind in
/// six surfaces that ship to a consumer: <c>ShadowDusk.HLSL</c>'s NuGet <c>Description</c>, the
/// LGPL source-availability notice in <c>THIRD-PARTY-NOTICES.txt</c>, the <c>SD0300</c> and
/// <c>SD1902</c> diagnostic texts, the <c>ShadowDusk.Wasm</c> MSBuild error, and the wasm
/// restore HOWTO. A release-time docs audit caught them, but an audit is a person remembering,
/// and this repo's own rule is that a check nobody remembers to run does not exist. The LGPL
/// notice is the one that matters most: naming the version actually distributed is a licence
/// obligation, not a nicety.</para>
///
/// <para>Deliberately NOT a list of "must say 2.1": the expected version is read from the
/// restore script, so the next bump repins one place and this test names every other place that
/// still disagrees.</para>
/// </summary>
public sealed class PinnedNativeVersionConsistencyTests
{
    // The authority: the release tag tools/restore.sh downloads the natives from.
    private static readonly Regex Vkd3dReleaseTag =
        new(@"releases/download/native-vkd3d-(?<version>\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

    // Any "vkd3d <version>" / "vkd3d-shader <version>" / "vkd3d-<version>.tar.xz" /
    // "native-vkd3d[-wasm]-<version>" claim.
    private static readonly Regex Vkd3dVersionClaim =
        new(@"(?:native-vkd3d(?:-wasm)?-|vkd3d(?:-shader)?[ -](?:\(libvkd3d-shader\), version )?)(?<version>\d+\.\d+(?:\.\d+)?)",
            RegexOptions.Compiled);

    /// <summary>
    /// Files that ship to a consumer or carry a licence obligation. A stale version here is
    /// visible outside the repo, which is what separates these from ordinary prose.
    /// </summary>
    public static TheoryData<string> ShippedSurfaces() => new()
    {
        // Renders on nuget.org for the package that carries the natives.
        "src/ShadowDusk.HLSL/ShadowDusk.HLSL.csproj",
        // LGPL-2.1+ source availability: must name the version actually distributed.
        "src/ShadowDusk.HLSL/THIRD-PARTY-NOTICES.txt",
        // Compiler output a consumer reads (SD0300).
        "src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs",
        // Runtime diagnostic (SD1902) + the MSBuild error a source-builder hits.
        "src/ShadowDusk.Wasm/WasmVkd3dShaderCompiler.cs",
        "src/ShadowDusk.Wasm/ShadowDusk.Wasm.csproj",
        // The restore HOWTO those two point at.
        "src/ShadowDusk.Wasm/wwwroot/vkd3d/RESTORE.md",
        // The supply-chain doc an auditor reads.
        "SECURITY.md",
        // The other half of the pin, which must not drift from restore.sh.
        "tools/restore.ps1",
    };

    [Theory]
    [MemberData(nameof(ShippedSurfaces))]
    public void ShippedSurface_NamesThePinnedVkd3dVersion(string relativePath)
    {
        string repoRoot = FindRepoRoot();
        string expected = PinnedVkd3dVersion(repoRoot);

        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"'{relativePath}' must exist, or this guard is silently vacuous");

        string[] lines = File.ReadAllLines(path);
        var stale = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match m in Vkd3dVersionClaim.Matches(lines[i]))
            {
                string found = m.Groups["version"].Value;
                if (found != expected)
                    stale.Add($"  {relativePath}:{i + 1} says '{m.Value}' (pinned: {expected})");
            }
        }

        stale.ShouldBeEmpty(
            $"'{relativePath}' ships to a consumer, so every vkd3d version it names must match the " +
            $"pinned native ({expected}, from tools/restore.sh's release tag). Stale:{Environment.NewLine}" +
            string.Join(Environment.NewLine, stale));
    }

    [Fact]
    public void BothRestoreScripts_PinTheSameVkd3dVersion()
    {
        string repoRoot = FindRepoRoot();
        string fromSh = PinnedVkd3dVersion(repoRoot);

        string ps1 = File.ReadAllText(Path.Combine(repoRoot, "tools", "restore.ps1"));
        Match m = Vkd3dReleaseTag.Match(ps1);
        m.Success.ShouldBeTrue("tools/restore.ps1 must download the natives from a native-vkd3d-<version> release tag");

        m.Groups["version"].Value.ShouldBe(fromSh,
            "restore.ps1 and restore.sh must pin the SAME vkd3d release, or a Windows host and a " +
            "Linux/macOS host silently compile with different compilers");
    }

    /// <summary>The version <c>tools/restore.sh</c> actually downloads. The single authority here.</summary>
    private static string PinnedVkd3dVersion(string repoRoot)
    {
        string sh = File.ReadAllText(Path.Combine(repoRoot, "tools", "restore.sh"));
        Match m = Vkd3dReleaseTag.Match(sh);
        m.Success.ShouldBeTrue("tools/restore.sh must download the natives from a native-vkd3d-<version> release tag");
        return m.Groups["version"].Value;
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }
}
