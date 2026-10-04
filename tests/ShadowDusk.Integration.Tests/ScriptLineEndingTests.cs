#nullable enable

using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Issue #357: bash rejects a CRLF script (<c>set -euo pipefail\r</c>), and a CRLF shebang line
/// names an interpreter that does not exist. <c>.gitattributes</c> pins <c>*.sh</c>,
/// <c>*.bash</c> and <c>*.py</c> to LF in the working tree on every OS; this pins the rules and
/// the checkout, so the tests that run <c>tools/*.sh</c> run the checked-out file, not a copy.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScriptLineEndingTests
{
    private static readonly string[] s_extensions = [".sh", ".bash", ".py"];

    /// <summary>
    /// The directories that hold the repo's own scripts, each scanned without recursing: below them are
    /// restored natives and untracked third-party clones (validation/FnaValidation/external, the
    /// Slang oracle) whose files are not ours and not under these rules.
    /// </summary>
    private static readonly string[] s_scriptDirectories =
        ["tools", Path.Combine("tools", "ci"), "validation", Path.Combine("validation", "FnaValidation"), Path.Combine(".github", "scripts")];

    /// <summary>The one-time fix for a checkout made before the rules existed.</summary>
    internal const string RefreshHint =
        "git does not rewrite an unchanged file when .gitattributes changes, so a checkout made before "
        + "the eol=lf rules keeps CRLF copies. Refresh them once: delete the listed files (commit or save "
        + "any local edits to them first), then run: git checkout -- '*.sh' '*.bash' '*.py'";

    internal static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }

    private static IEnumerable<string> Scripts(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).Where(f => s_extensions.Contains(Path.GetExtension(f), StringComparer.Ordinal))
            : [];

    [Fact]
    public void GitAttributes_PinsEveryScriptTypeToLf()
    {
        string[] rules = File.ReadAllLines(Path.Combine(RepoRoot(), ".gitattributes"))
            .Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .ToArray();

        foreach (string ext in s_extensions)
            rules.ShouldContain($"*{ext} text eol=lf", $"*{ext} has no eol=lf rule in .gitattributes");
    }

    [Fact]
    public void EveryCheckedOutScript_HasLfLineEndings()
    {
        string root = RepoRoot();
        var scripts = s_scriptDirectories.SelectMany(r => Scripts(Path.Combine(root, r))).ToList();
        scripts.ShouldContain(p => p.EndsWith("verify-slang-nupkg.sh", StringComparison.Ordinal),
            "the scan did not find tools/verify-slang-nupkg.sh; the walk is broken");
        scripts.ShouldContain(p => p.EndsWith("compare.py", StringComparison.Ordinal),
            "the scan did not find validation/compare.py; the walk is broken");

        var crlf = scripts
            .Where(p => File.ReadAllBytes(p).Contains((byte)'\r'))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .ToList();

        crlf.ShouldBeEmpty(RefreshHint);
    }
}
