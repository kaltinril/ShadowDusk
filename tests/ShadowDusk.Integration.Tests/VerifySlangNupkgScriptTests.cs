#nullable enable

using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Issue #226: <c>tools/verify-slang-nupkg.sh</c> is the release gate that fails a pack which
/// silently lost a slangc native (the csproj's pack entries are <c>Exists()</c>-conditioned).
/// A gate nobody tests can rot into one that always exits 0, so these run the real script
/// against synthetic nupkgs, pin its entry list to the csproj that produces the package, and
/// check release.yml still runs it as a hard failure.
/// </summary>
public sealed class VerifySlangNupkgScriptTests : IDisposable
{
    private const string NoticesEntry = "THIRD-PARTY-NOTICES.txt";

    private readonly string _work = Path.Combine(Path.GetTempPath(), "sd_verify_nupkg_" + Guid.NewGuid().ToString("N"));

    public VerifySlangNupkgScriptTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }

    private static string ScriptPath() => Path.Combine(RepoRoot(), "tools", "verify-slang-nupkg.sh");

    private static string ScriptText() => File.ReadAllText(ScriptPath());

    /// <summary>The entry names the script requires, in script order (the quoted lines of its for-list).</summary>
    private static List<string> ScriptEntries()
    {
        string text = ScriptText();
        int start = text.IndexOf("for entry in", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "the script's 'for entry in' list is gone");
        int end = text.IndexOf("; do", start, StringComparison.Ordinal);
        var entries = Regex.Matches(text[start..end], @"'([^']+)'").Select(m => m.Groups[1].Value).ToList();
        entries.ShouldNotBeEmpty();
        return entries;
    }

    internal static string? FindBash()
    {
        // Git Bash first on Windows: a bare 'bash' there is often WSL's launcher, which cannot
        // see Windows paths the way the script needs.
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe"]
            : ["/bin/bash", "/usr/bin/bash"];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Runs the script on <paramref name="entries"/>; only called where <see cref="FindBash"/> found one.</summary>
    private async Task<ChildProcessResult> RunScriptAsync(IEnumerable<string> entries)
    {
        string bash = FindBash() ?? throw new InvalidOperationException("bash is not available");

        // The checked-out script runs as is: .gitattributes pins *.sh to LF (issue #357), so a
        // Windows checkout no longer holds it as CRLF, which bash rejects ('set -euo pipefail\r').
        // Forward slashes: Git Bash takes 'C:/...' but reads '\' as an escape.
        string script = ScriptPath().Replace('\\', '/');
        ScriptText().ShouldNotContain("\r", Case.Sensitive, ScriptLineEndingTests.RefreshHint);

        string package = Path.Combine(_work, "ShadowDusk.Slang.0.0.0.nupkg");
        if (File.Exists(package))
            File.Delete(package);
        using (ZipArchive zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            foreach (string entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write("x");
            }
        }

        var start = new ProcessStartInfo(bash) { WorkingDirectory = _work };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(Path.GetFileName(package));
        return await ChildProcess.RunAsync(start, TimeSpan.FromSeconds(60), "verify-slang-nupkg.sh");
    }

    [BashFact]
    public async Task CompletePackage_ExitsZero()
    {
        var result = await RunScriptAsync([.. ScriptEntries()]);

        result.ExitCode.ShouldBe(0, result.Output);
        result.Stdout.ShouldContain("all 8 slangc natives", Case.Sensitive);
    }

    [BashTheory]
    [InlineData("runtimes/osx-x64/native/slangc")]
    [InlineData("runtimes/win-x64/native/slang-compiler.dll")]
    [InlineData("runtimes/linux-x64/native/slangc")]
    public async Task PackageMissingANative_ExitsOne_NamingIt(string missing)
    {
        var result = await RunScriptAsync(ScriptEntries().Where(e => e != missing));

        result.ExitCode.ShouldBe(1, result.Output);
        result.Stderr.ShouldContain("is missing " + missing, Case.Sensitive);
        result.Stderr.ShouldContain("SD0621", Case.Sensitive);
    }

    [BashFact]
    public async Task PackageMissingTheNotices_ExitsOne_NamingIt()
    {
        var result = await RunScriptAsync(ScriptEntries().Where(e => e != NoticesEntry));

        result.ExitCode.ShouldBe(1, result.Output);
        result.Stderr.ShouldContain("is missing " + NoticesEntry, Case.Sensitive);
    }

    [BashFact]
    public async Task NativeNestedInAFolderOfTheSameName_IsNotAMatch()
    {
        // Issue #225: NuGet packs a folder-less PackagePath as '.../native/slangc/slangc'. A
        // substring match once passed that; the exact whole-line match must not.
        string wrong = "runtimes/osx-x64/native/slangc/slangc";
        var result = await RunScriptAsync(
            ScriptEntries().Where(e => e != "runtimes/osx-x64/native/slangc").Append(wrong));

        result.ExitCode.ShouldBe(1, result.Output);
        result.Stderr.ShouldContain("is missing runtimes/osx-x64/native/slangc", Case.Sensitive);
    }

    [Fact]
    public void ScriptsEntryList_MatchesTheCsprojsPackEntriesAndNativeVersion()
    {
        string csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ShadowDusk.Slang", "ShadowDusk.Slang.csproj"));
        string version = Regex.Match(csproj, @"<SlangNativeVersion>([^<]+)</SlangNativeVersion>").Groups[1].Value;
        version.ShouldNotBeNullOrEmpty("SlangNativeVersion is gone from ShadowDusk.Slang.csproj");

        // Every packed native: file name from Include (after the last backslash), folder from
        // PackagePath. The notice has no folder.
        var expected = new List<string>();
        foreach (Match item in Regex.Matches(
                     csproj, @"<None\s+Include=""(?<include>[^""]+)""[^>]*?PackagePath=""(?<path>[^""]+)""", RegexOptions.Singleline))
        {
            string include = item.Groups["include"].Value.Replace("$(SlangNativeVersion)", version, StringComparison.Ordinal);
            string file = include[(include.LastIndexOfAny(['\\', '/']) + 1)..];
            string path = item.Groups["path"].Value;
            // README.md and friends are packed too, but the gate only guards natives and the notice.
            if (path.StartsWith("runtimes/", StringComparison.Ordinal))
                expected.Add(path.EndsWith('/') ? path + file : path);
            else if (file == NoticesEntry)
                expected.Add(NoticesEntry);
        }
        expected.ShouldContain(NoticesEntry);
        expected.Count(e => e.StartsWith("runtimes/", StringComparison.Ordinal)).ShouldBe(8, "4 RIDs x (slangc + slang-compiler library)");

        ScriptEntries().ShouldBe(expected, ignoreOrder: true,
            "tools/verify-slang-nupkg.sh and ShadowDusk.Slang.csproj disagree on what the package must contain");

        // The version the script's library names carry, and the other two places that pin it.
        ScriptText().ShouldContain("0." + version, Case.Sensitive);
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "ShadowDusk.Slang", "SlangToolPath.cs"))
            .ShouldContain($"SlangVersion = \"{version}\"", Case.Sensitive);
    }

    [Fact]
    public void ReleaseWorkflow_RunsTheGate_WithoutContinueOnError()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoRoot(), ".github", "workflows", "release.yml"));
        int run = Array.FindIndex(lines, l => l.Contains("./tools/verify-slang-nupkg.sh", StringComparison.Ordinal));
        run.ShouldBeGreaterThan(-1, "release.yml no longer runs tools/verify-slang-nupkg.sh");

        // The step is the YAML block starting at the nearest '- name:' above the run line and
        // ending at the next one (or the end of the file).
        int first = run;
        while (first > 0 && !lines[first].TrimStart().StartsWith("- name:", StringComparison.Ordinal))
            first--;
        int last = run + 1;
        while (last < lines.Length && !lines[last].TrimStart().StartsWith("- name:", StringComparison.Ordinal))
            last++;
        string step = string.Join('\n', lines[first..last]);

        step.ShouldNotContain("continue-on-error", Case.Sensitive);
        step.ShouldNotContain("|| true", Case.Sensitive);
    }
}

/// <summary>An xUnit fact that skips on a host with no usable bash.</summary>
public sealed class BashFactAttribute : FactAttribute
{
    public BashFactAttribute()
    {
        if (VerifySlangNupkgScriptTests.FindBash() is null)
            Skip = "bash is not available on this host";
    }
}

/// <summary>An xUnit theory that skips on a host with no usable bash.</summary>
public sealed class BashTheoryAttribute : TheoryAttribute
{
    public BashTheoryAttribute()
    {
        if (VerifySlangNupkgScriptTests.FindBash() is null)
            Skip = "bash is not available on this host";
    }
}
