#nullable enable

using System.Text.RegularExpressions;
using Shouldly;
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
    // Codes emitted as a literal argument anywhere in src/: named (`Code: "SD0123"`) or passed
    // positionally to a converter's Fail(...) helper (`Fail(file, "SD0123", ...)`), which the
    // named-only form could not see. Also a code held in a constant (`const string X = "SD0219";`),
    // which a `Code: SomeConst` call site hides from the first two forms.
    private static readonly Regex EmittedCode =
        new(@"(?:Code:\s*|[(,]\s*|const\s+string\s+\w+\s*=\s*)""(?<code>(?:SD|FX|X)\d{4})""", RegexOptions.Compiled);

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

        emitted.ShouldNotBeEmpty("the scan must actually find codes, or this test is vacuous");

        var missing = emitted.Except(registered).OrderBy(c => c, StringComparer.Ordinal).ToList();

        missing.ShouldBeEmpty(
            "every diagnostic code must be registered in docs/error-codes.md, which is published "
            + "as the consumer-facing Diagnostic Codes page. Missing: " + string.Join(", ", missing));
    }

    // A row that DEFINES one code: the first cell is exactly that code. A range row
    // (`SD1900`–`SD1999`) has more in its first cell and is not a definition.
    private static readonly Regex DefinitionRow =
        new(@"^\|\s*`(?<code>(?:SD|FX|X)\d{4})`\s*\|", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// One code, one meaning. Two branches that each take "the next free code" both register
    /// it, and git merges the two new table rows without a conflict: that is how <c>SD1906</c>
    /// came to mean both "target not supported by the browser host" (issue #272) and "a WASM
    /// module trapped" (issue #271) on one tree. A consumer looking the code up then finds two
    /// answers. The per-section tables must define each code exactly once.
    /// </summary>
    [Fact]
    public void EveryCode_IsDefinedOnce_InTheRegistry()
    {
        string registry = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "error-codes.md"));

        // The "Ranges" overview table names a range by its first code (and lists the one-code
        // range `SD0000`), so it is not where codes are defined: start at the first section after it.
        int ranges = registry.IndexOf("\n## Ranges", StringComparison.Ordinal);
        ranges.ShouldBeGreaterThanOrEqualTo(0, "the registry's 'Ranges' section moved or was renamed");
        int definitions = registry.IndexOf("\n## ", ranges + 1, StringComparison.Ordinal);
        definitions.ShouldBeGreaterThan(ranges, "no section follows 'Ranges' in the registry");

        var rows = DefinitionRow.Matches(registry[definitions..])
            .Select(m => m.Groups["code"].Value)
            .ToList();
        rows.Count.ShouldBeGreaterThan(50, "the scan must actually find definition rows, or this test is vacuous");

        var duplicated = rows.GroupBy(c => c, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} (x{g.Count()})")
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        duplicated.ShouldBeEmpty(
            "each diagnostic code must have exactly one row in docs/error-codes.md; a second row means "
            + "two changes took the same number. Renumber one of them. Duplicated: " + string.Join(", ", duplicated));
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
