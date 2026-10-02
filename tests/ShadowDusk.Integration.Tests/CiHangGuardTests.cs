#nullable enable

using System.Globalization;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Issue #312: the CI hang guard is two timeouts in two places, and they only work as a pair.
/// <c>--blame-hang-timeout</c> kills a stalled test host and dumps its threads; the VSTest
/// <c>TestSessionTimeout</c> aborts the run with no dump at all. The blame timer is an
/// inactivity timer that every starting or finishing test resets, so it fires that long after
/// the LAST test event; the session timeout is absolute. At 3 minutes against 5, any stall
/// whose last test event came after minute 2 lost the race, and a healthy run of this suite is
/// longer than that, so a late stall never produced a dump (measured on this suite with a
/// deliberately hung test: session cap first, no dump; session cap raised, dump and sequence
/// file). This test reads the workflow and fails when the pair stops satisfying
/// <c>session &gt;= healthy run + blame timeout + dump time</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CiHangGuardTests
{
    /// <summary>
    /// The longest a healthy assembly run is allowed to be assumed. Measured worst on CI is
    /// 3 min 20 s (<c>ShadowDusk.Integration.Tests</c>, windows-latest, 2026-10-02). If the
    /// suite outgrows this, raise it AND the session timeout: do not shrink the margin.
    /// </summary>
    private static readonly TimeSpan HealthyAssemblyRun = TimeSpan.FromMinutes(4);

    /// <summary>Time for the blame collector to dump the host and its children once it fires.</summary>
    private static readonly TimeSpan DumpTime = TimeSpan.FromMinutes(1);

    [Fact]
    public void EveryBlameHangStep_LeavesTheSessionTimeoutRoomForALateStallToBeDumped()
    {
        string repoRoot = FindRepoRoot();
        TimeSpan runsettingsSession = RunsettingsSessionTimeout(Path.Combine(repoRoot, "ShadowDusk.runsettings"));

        var guarded = new List<string>();
        foreach (string workflow in Directory.GetFiles(Path.Combine(repoRoot, ".github", "workflows"), "*.yml"))
        {
            foreach (string step in Steps(File.ReadAllText(workflow)))
            {
                Match blame = Regex.Match(step, @"--blame-hang-timeout\s+(\d+)(ms|s|m|h)\b");
                if (!blame.Success)
                    continue;

                string where = $"{Path.GetFileName(workflow)}: {step.Split('\n')[0].Trim()}";
                guarded.Add(where);

                TimeSpan blameTimeout = Duration(blame.Groups[1].Value, blame.Groups[2].Value);

                // The inline override wins over the --settings file; without one the file applies.
                Match inline = Regex.Match(step, @"RunConfiguration\.TestSessionTimeout=(\d+)");
                TimeSpan session = inline.Success
                    ? TimeSpan.FromMilliseconds(long.Parse(inline.Groups[1].Value, CultureInfo.InvariantCulture))
                    : runsettingsSession;

                TimeSpan needed = HealthyAssemblyRun + blameTimeout + DumpTime;
                session.ShouldBeGreaterThanOrEqualTo(needed,
                    $"{where}: TestSessionTimeout {session.TotalMinutes:0.#} min cannot outlast a stall that starts late in " +
                    $"a healthy run ({HealthyAssemblyRun.TotalMinutes:0.#} min) plus the {blameTimeout.TotalMinutes:0.#} min " +
                    $"blame-hang timeout plus {DumpTime.TotalMinutes:0.#} min to write the dump, so the session cap would " +
                    "abort the run with no hang dump (issue #312). Raise the session timeout or lower the blame timeout.");

                // A step timeout shorter than the session cap would pre-empt the dump the same way.
                Match stepTimeout = Regex.Match(step, @"^\s*timeout-minutes:\s*(\d+)", RegexOptions.Multiline);
                stepTimeout.Success.ShouldBeTrue(
                    $"{where}: needs its own timeout-minutes. A step timeout fails the step, so the hang-dump upload still " +
                    "runs; only a job timeout would bound it otherwise, and that cancels the uploads.");
                TimeSpan.FromMinutes(int.Parse(stepTimeout.Groups[1].Value, CultureInfo.InvariantCulture))
                    .ShouldBeGreaterThan(session, $"{where}: the step timeout must not fire before the session timeout");
            }
        }

        guarded.ShouldNotBeEmpty("no workflow step passes --blame-hang-timeout any more: the integration lane has lost its hang guard");
    }

    /// <summary>Splits a workflow into its steps (each starts at a <c>- name:</c> line).</summary>
    private static IEnumerable<string> Steps(string workflow) =>
        Regex.Split(workflow.Replace("\r\n", "\n", StringComparison.Ordinal), @"(?m)^(?=\s*- name:)")
            .Where(step => step.TrimStart().StartsWith("- name:", StringComparison.Ordinal))
            // Comment lines describe the flags; only the YAML that runs counts.
            .Select(step => string.Join('\n', step.Split('\n').Where(line => !line.TrimStart().StartsWith('#'))));

    private static TimeSpan Duration(string value, string unit)
    {
        double number = double.Parse(value, CultureInfo.InvariantCulture);
        return unit switch
        {
            "ms" => TimeSpan.FromMilliseconds(number),
            "s" => TimeSpan.FromSeconds(number),
            "m" => TimeSpan.FromMinutes(number),
            "h" => TimeSpan.FromHours(number),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "unknown blame-hang-timeout unit"),
        };
    }

    private static TimeSpan RunsettingsSessionTimeout(string path)
    {
        Match match = Regex.Match(File.ReadAllText(path), @"<TestSessionTimeout>(\d+)</TestSessionTimeout>");
        match.Success.ShouldBeTrue($"{path} no longer sets TestSessionTimeout");
        return TimeSpan.FromMilliseconds(long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
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
