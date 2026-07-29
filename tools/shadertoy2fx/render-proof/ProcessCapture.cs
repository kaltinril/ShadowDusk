using System;
using System.Diagnostics;
using System.Text;

namespace ShadowDusk.ShaderToy.RenderProof;

/// <summary>
/// Runs a child process and captures both output streams without deadlocking.
/// </summary>
/// <remarks>
/// Every driver in this project used to do:
/// <code>
///     string stdout = proc.StandardOutput.ReadToEnd();
///     string stderr = proc.StandardError.ReadToEnd();   // only reached at stdout EOF
///     proc.WaitForExit();
/// </code>
/// That deadlocks whenever the child writes more to stderr than the pipe buffer holds
/// (~4 KB): the child blocks writing stderr, so it never exits, so stdout never reaches
/// EOF, so the first <c>ReadToEnd</c> never returns. Nobody hit it while the CLI was
/// quiet, but Phase 53 made warnings print by default, and a ShaderToy shader that trips
/// SD0402 on a dozen loops now emits far more than 4 KB - `infinite_cube_starfield.glsl`
/// hung the fidelity runner indefinitely (found 2026-07-28). The compiler was never
/// implicated: the same file compiles in 0.38 s when run directly.
///
/// Draining both pipes concurrently via the event callbacks is the fix, and it keeps the
/// callers synchronous (no sync-over-async).
/// </remarks>
internal static class ProcessCapture
{
    /// <summary>Starts <paramref name="psi"/>, waits for exit, returns its streams.</summary>
    /// <remarks>
    /// <paramref name="psi"/> must already have <c>RedirectStandardOutput</c>,
    /// <c>RedirectStandardError</c>, and <c>UseShellExecute = false</c> set.
    /// </remarks>
    public static (int ExitCode, string StdOut, string StdErr) Run(ProcessStartInfo psi)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the ShadowDusk CLI process.");

        // Both handlers fire on background threads, so neither pipe can back up behind the other.
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        proc.WaitForExit();
        // Parameterless WaitForExit() after an exit signal also flushes the async readers,
        // so the builders are complete by the time we read them.

        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
