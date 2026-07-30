#nullable enable

using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Standing guard (issue #171): FluentAssertions must never come back. Shouldly is the
/// project's only assertion library.
///
/// The reason is a LICENCE, not a preference. FluentAssertions relicensed at 8.x to the
/// Xceed <i>"Community License Agreement (for Non-Commercial Use)"</i>, which requires a
/// paid commercial licence for any use by an organisation that earns revenue. We were
/// pinned at 7.2.2 (the last Apache-2.0 release), but a pin only defers the problem: the
/// 7.x line gets no further fixes, so the choice was "migrate now" or "migrate later, from
/// a frozen dependency". Shouldly is BSD-3-Clause at every version, with no commercial gate.
///
/// This fails loudly if a future edit reintroduces the package reference or the namespace
/// anywhere in the repository - including "just for one test". See project_decisions.md.
/// </summary>
public sealed class NoFluentAssertionsTests
{
    /// <summary>Directories that are build output or restored third-party content, not our source.</summary>
    private static readonly string[] IgnoredDirectories =
        ["obj", "bin", ".git", ".vs", "tools", "packages", ".wasm-build", "node_modules", "TestResults"];

    /// <summary>
    /// Files that legitimately mention the name while documenting WHY it is banned.
    /// Anything not on this list must not mention it at all.
    /// </summary>
    private static readonly string[] AllowedToMentionByName =
    [
        "NoFluentAssertionsTests.cs",   // this file
        "Directory.Packages.props",     // the ban comment on the Test ItemGroup
        "CHANGELOG.md",
        "CLAUDE.md",
        "README.md",
        "project_facts.md",
        "project_rules.md",
        "project_decisions.md",
    ];

    [Fact]
    public void NoProjectReferencesFluentAssertions()
    {
        var offenders = new List<string>();

        foreach (string file in EnumerateRepoFiles(["*.csproj", "*.props", "*.targets", "packages.lock.json"]))
        {
            if (IsAllowedToMentionByName(file))
                continue;

            if (Regex.IsMatch(File.ReadAllText(file), "FluentAssertions", RegexOptions.IgnoreCase))
                offenders.Add(Relative(file));
        }

        offenders.ShouldBeEmpty(
            "FluentAssertions is banned (issue #171): 8.x requires a paid commercial licence and 7.x is " +
            "frozen. Use Shouldly (BSD-3-Clause). Offending files: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoSourceFileUsesFluentAssertions()
    {
        var offenders = new List<string>();

        foreach (string file in EnumerateRepoFiles(["*.cs"]))
        {
            if (IsAllowedToMentionByName(file))
                continue;

            string text = File.ReadAllText(file);

            // The namespace import, and FluentAssertions' `.Should()` entry point - the
            // latter catches a copy-pasted assertion even without the using directive.
            if (Regex.IsMatch(text, @"\busing\s+FluentAssertions\b") ||
                Regex.IsMatch(text, @"\.Should\(\)"))
            {
                offenders.Add(Relative(file));
            }
        }

        offenders.ShouldBeEmpty(
            "FluentAssertions is banned (issue #171). Shouldly's entry points are ShouldBe/ShouldContain/... " +
            "on the value itself, never `.Should()`. Offending files: " + string.Join(", ", offenders));
    }

    // -------------------------------------------------------------------------

    private static IEnumerable<string> EnumerateRepoFiles(string[] patterns)
    {
        string root = FindRepoRoot();

        foreach (string pattern in patterns)
        {
            foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .Any(segment => IgnoredDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static bool IsAllowedToMentionByName(string file) =>
        AllowedToMentionByName.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);

    private static string Relative(string file) =>
        Path.GetRelativePath(FindRepoRoot(), file).Replace('\\', '/');

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
