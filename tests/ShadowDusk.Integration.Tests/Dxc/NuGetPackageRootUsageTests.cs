#nullable enable

using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// Issue #362: with a custom NuGet global-packages folder (<c>NUGET_PACKAGES</c> or
/// <c>globalPackagesFolder</c>, e.g. <c>F:\packages</c>) MSBuild's <c>$(NuGetPackageRoot)</c> has
/// no trailing slash, so <c>$(NuGetPackageRoot)vortice.dxc.native/...</c> globbed
/// <c>F:\packagesvortice.dxc.native</c>, matched nothing, and the foreign-DXC tests failed with
/// "fixture is missing" instead of running.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NuGetPackageRootUsageTests
{
    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }

    /// <summary>Directories never holding a project file of ours (build output, scratch, caches).</summary>
    private static readonly HashSet<string> s_skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", ".wasm-build", ".vs", ".claude",
    };

    private static IEnumerable<string> MsbuildFiles(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (Path.GetExtension(file) is ".csproj" or ".props" or ".targets")
                yield return file;
        }
        foreach (string sub in Directory.EnumerateDirectories(directory))
        {
            if (s_skipped.Contains(Path.GetFileName(sub)))
                continue;
            foreach (string file in MsbuildFiles(sub))
                yield return file;
        }
    }

    [Fact]
    public void EveryMsbuildPathBuiltOnNuGetPackageRoot_EnsuresATrailingSlash()
    {
        string root = RepoRoot();
        var offenders = new List<string>();
        int scanned = 0;
        foreach (string file in MsbuildFiles(root))
        {
            scanned++;
            // Comments (which may describe the bug) are blanked, keeping line numbers.
            string text = Regex.Replace(File.ReadAllText(file), "<!--.*?-->",
                m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                // Allowed: the EnsureTrailingSlash wrapper, and an emptiness test in a Condition.
                string rest = Regex.Replace(lines[i], @"\$\(\[MSBuild\]::EnsureTrailingSlash\('\$\(NuGetPackageRoot\)'\)\)", "");
                rest = Regex.Replace(rest, @"'\$\(NuGetPackageRoot\)'\s*(!=|==)\s*''", "");
                if (rest.Contains("$(NuGetPackageRoot)", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        scanned.ShouldBeGreaterThan(20, "the scan found almost no project files; the walk is broken");
        offenders.ShouldBeEmpty(
            "wrap $(NuGetPackageRoot) in $([MSBuild]::EnsureTrailingSlash('$(NuGetPackageRoot)')) before appending a path");
    }

    [Fact]
    public void TheForeignDxcProbe_IsStampedWithASeparatorBeforeThePackageName_AndExists()
    {
        string? probed = typeof(NuGetPackageRootUsageTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ForeignDxcPackageRuntimes")?.Value;

        probed.ShouldNotBeNullOrEmpty("the build stamped no ForeignDxcPackageRuntimes (NuGetPackageRoot was empty)");
        probed.ShouldEndWith("vortice.dxc.native/1.0.5/runtimes/", Case.Sensitive);
        string root = probed[..^"vortice.dxc.native/1.0.5/runtimes/".Length];
        root.ShouldNotBeEmpty();
        (root.EndsWith('/') || root.EndsWith('\\')).ShouldBeTrue($"no separator between the package root and the package: {probed}");
        // PackageDownload restores the package on every OS (only its natives are per-RID).
        Directory.Exists(probed).ShouldBeTrue($"the restored Vortice.Dxc.Native 1.0.5 is not at {probed}");

        ForeignDxc.MissingFixture("x").ShouldContain(probed, Case.Sensitive);
    }
}
