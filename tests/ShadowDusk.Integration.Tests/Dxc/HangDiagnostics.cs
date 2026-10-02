#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// Best-effort native evidence from a hung probe child, captured BEFORE the watchdog kills
/// it: every thread's native stack (gdb on Linux, <c>sample</c> on macOS, when available),
/// each Linux thread's kernel wait channel (no debugger needed), and a .NET
/// <c>createdump</c> core. <c>dotnet test --blame-hang</c> only dumps the test host, never
/// this grandchild, so without this a probe hang leaves nothing to diagnose.
/// </summary>
/// <remarks>
/// Files go to <c>SHADOWDUSK_HANG_DUMP_DIR</c> (CI points it under <c>TestResults/</c>, which
/// the integration job uploads on failure), else the temp directory. Attaching needs ptrace
/// rights; CI's Linux lane lowers <c>kernel.yama.ptrace_scope</c> to 0 for that. Every tool
/// failure is recorded in the report, never thrown: the hang is the finding.
/// </remarks>
internal static class HangDiagnostics
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(45);

    public static async Task<string> CaptureAsync(int pid, string label)
    {
        string dir = Environment.GetEnvironmentVariable("SHADOWDUSK_HANG_DUMP_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "shadowdusk-hang-dumps");
        Directory.CreateDirectory(dir);

        var report = new StringBuilder();
        report.AppendLine($"== {label}: hung probe pid {pid}");

        if (OperatingSystem.IsLinux())
        {
            report.AppendLine(KernelWaitChannels(pid));
            if (FindOnPath("gdb") is { } gdb)
                report.AppendLine(await RunToolAsync(gdb, "-p", pid.ToString(), "-batch", "-nx", "-ex", "thread apply all bt"));
            else
                report.AppendLine("gdb: not installed (no native stacks; see the createdump core)");
        }
        else if (OperatingSystem.IsMacOS())
        {
            report.AppendLine(await RunToolAsync("/usr/bin/sample", pid.ToString(), "1"));
        }

        string createdump = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "createdump");
        string core = Path.Combine(dir, $"probe-{pid}.dmp");
        report.AppendLine(File.Exists(createdump)
            ? await RunToolAsync(createdump, "-f", core, pid.ToString())
            : "createdump: not found next to the runtime");

        string text = report.ToString();
        string reportPath = Path.Combine(dir, $"probe-{pid}-stacks.txt");
        await File.WriteAllTextAsync(reportPath, text);
        return $"(native evidence: {reportPath}; core: {core})\n{text}";
    }

    private static string KernelWaitChannels(int pid)
    {
        var sb = new StringBuilder("threads (name: kernel wait channel):\n");
        try
        {
            foreach (string task in Directory.GetDirectories($"/proc/{pid}/task"))
            {
                string comm = ReadOrEmpty(Path.Combine(task, "comm"));
                string wchan = ReadOrEmpty(Path.Combine(task, "wchan"));
                sb.Append("  ").Append(Path.GetFileName(task)).Append(' ').Append(comm).Append(": ").AppendLine(wchan);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sb.AppendLine("  unreadable: " + ex.Message);
        }
        return sb.ToString();
    }

    private static string ReadOrEmpty(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "?"; }
    }

    private static string? FindOnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, tool))
            .FirstOrDefault(File.Exists);

    private static async Task<string> RunToolAsync(string tool, params string[] arguments)
    {
        var psi = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) psi.ArgumentList.Add(argument);

        try
        {
            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("did not start");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(ToolTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return $"{Path.GetFileName(tool)}: timed out after {ToolTimeout.TotalSeconds:0} s";
            }
            return $"--- {Path.GetFileName(tool)} (exit {process.ExitCode})\n{await stdout}{await stderr}";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"{Path.GetFileName(tool)}: could not run ({ex.Message})";
        }
    }
}
