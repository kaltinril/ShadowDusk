#nullable enable

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Guards <c>docs/error-codes.md</c>, the diagnostic registry, against drift.
///
/// <para>It is published as a page on the documentation site, so a code that ShadowDusk can
/// emit but the registry does not list is a code a consumer sees in their build output and
/// cannot look up. That has already happened twice: five codes were missing when the registry
/// was first published, and <c>X0011</c> was missing after that. CLAUDE.md's rule is that a new
/// code is registered in the same change that adds it — this test is what makes forgetting
/// fail loudly instead of silently shipping.</para>
/// </summary>
public sealed class DiagnosticCodeRegistryTests
{
    // Codes emitted as a literal `Code: "SD0123"` argument anywhere in src/.
    private static readonly Regex EmittedCode =
        new(@"Code:\s*""(?<code>(?:SD|FX|X)\d{4})""", RegexOptions.Compiled);

    // Codes the FX9 pre-parser builds from its enum (`FX` + the enum's numeric value).
    private static readonly Regex FxEnumMember =
        new(@"^\s*\w+\s*=\s*(?<value>\d+)\s*,", RegexOptions.Compiled | RegexOptions.Multiline);

    // A registry row: | `SD0123` | ... |
    private static readonly Regex RegisteredCode =
        new(@"\|\s*`(?<code>(?:SD|FX|X)\d{4})`", RegexOptions.Compiled);

    [Fact]
    public void EveryCodeShadowDuskCanEmit_IsListedInTheRegistry()
    {
        string repoRoot = FindRepoRoot();
        var registered = CollectRegistered(repoRoot);
        var emitted = CollectEmitted(repoRoot);

        emitted.Should().NotBeEmpty("the scan must actually find codes, or this test is vacuous");

        var missing = emitted.Except(registered).OrderBy(c => c, StringComparer.Ordinal).ToList();

        missing.Should().BeEmpty(
            "every diagnostic code must be registered in docs/error-codes.md, which is published "
            + "as the consumer-facing Diagnostic Codes page. Missing: " + string.Join(", ", missing));
    }

    private static HashSet<string> CollectRegistered(string repoRoot)
    {
        string registry = File.ReadAllText(Path.Combine(repoRoot, "docs", "error-codes.md"));
        return RegisteredCode.Matches(registry)
            .Select(m => m.Groups["code"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> CollectEmitted(string repoRoot)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            // bin/obj carry copies of the same sources; scanning them just duplicates work.
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);

            foreach (Match m in EmittedCode.Matches(text))
                codes.Add(m.Groups["code"].Value);

            // The pre-parser's codes never appear as literals — they are formatted from the
            // enum's numeric value, so scan the enum itself.
            if (Path.GetFileName(file) == "FxParseErrorCode.cs")
            {
                foreach (Match m in FxEnumMember.Matches(text))
                    codes.Add($"FX{int.Parse(m.Groups["value"].Value):D4}");
            }
        }

        return codes;
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
