#nullable enable

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Standing guard (Phase 47): no shipped <c>ShadowDusk.*</c> product library under <c>src/</c> may take
/// a MonoGame dependency. MonoGame is a consumer's runtime, not ours — pulling it into a product package
/// would bloat every consumer's graph and pin a MonoGame version into the product, violating the
/// "do not bump / do not couple MonoGame" directive. The ShaderToy converter is pure-managed; its
/// MonoGame runtime helper + sample live under <c>samples/</c>, never <c>src/</c>. This test fails loudly
/// if a future edit quietly adds a <c>MonoGame.Framework.*</c> reference to any <c>src/*.csproj</c>.
/// </summary>
public sealed class NoMonoGameInProductLibrariesTests
{
    [Fact]
    public void NoSrcProjectReferencesMonoGame()
    {
        string srcDir = Path.Combine(FindRepoRoot(), "src");
        Directory.Exists(srcDir).Should().BeTrue($"the product source tree must exist at {srcDir}");

        var offenders = new List<string>();
        foreach (string csproj in Directory.EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(csproj);
            if (Regex.IsMatch(text, @"MonoGame\.Framework", RegexOptions.IgnoreCase))
                offenders.Add(Path.GetFileName(csproj));
        }

        offenders.Should().BeEmpty(
            "no shipped ShadowDusk.* product library may depend on MonoGame; the runtime helper + " +
            "sample belong under samples/ (see the ShaderToy sample-migration item in plan/PHASE-51). Offending projects: " +
            string.Join(", ", offenders));
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo dir = new(AppContext.BaseDirectory);
        while (dir.Parent is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root (the directory containing ShadowDusk.slnx).");
    }
}
