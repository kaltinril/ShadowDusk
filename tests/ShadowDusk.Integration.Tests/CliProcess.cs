#nullable enable

using System.Diagnostics;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Runs the ShadowDuskCLI executable for a test, through <see cref="ChildProcess"/>: both
/// pipes drained concurrently, the process tree killed on timeout, and a timeout that reports
/// what the CLI was doing (with a dump of it) instead of a bare cancellation (issue #316).
/// </summary>
internal static class CliProcess
{
    /// <param name="executable">The CLI to run (normally <see cref="CliBinaryFixture.ExecutablePath"/>).</param>
    /// <param name="arguments">Passed verbatim, one argument each; <see langword="null"/> entries are skipped.</param>
    /// <param name="timeout">Budget for the whole run.</param>
    /// <param name="configure">Last word on the start info, e.g. to prepend to <c>PATH</c>.</param>
    /// <param name="cancellationToken">The calling test's own deadline, if it has one.</param>
    public static Task<ChildProcessResult> RunAsync(
        string executable,
        IEnumerable<string?> arguments,
        TimeSpan timeout,
        Action<ProcessStartInfo>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (string? argument in arguments)
        {
            if (argument is not null)
                psi.ArgumentList.Add(argument);
        }
        configure?.Invoke(psi);

        return ChildProcess.RunAsync(
            psi, timeout, "ShadowDuskCLI", captureHangEvidence: true, cancellationToken: cancellationToken);
    }
}
