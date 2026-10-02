#nullable enable

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Best-effort native evidence from a hung child process, captured BEFORE the watchdog kills
/// it: every thread's native stack (gdb on Linux, <c>sample</c> on macOS, when available),
/// each Linux thread's kernel wait channel (no debugger needed), and a dump: .NET's
/// <c>createdump</c> on Linux and macOS, <c>MiniDumpWriteDump</c> on Windows (where
/// <c>createdump.exe</c> refuses a pid: "The pid argument is no longer supported").
/// <c>dotnet test --blame-hang</c> only dumps the test host and what is still its child when
/// the host itself stalls; a child that a test's own timeout kills first would otherwise leave
/// nothing to diagnose.
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

    /// <param name="pid">The hung process.</param>
    /// <param name="label">Names the process in the report.</param>
    /// <param name="directory">Where the files go; defaults to <c>SHADOWDUSK_HANG_DUMP_DIR</c>, then temp.</param>
    public static async Task<string> CaptureAsync(int pid, string label, string? directory = null)
    {
        string dir = directory
            ?? (Environment.GetEnvironmentVariable("SHADOWDUSK_HANG_DUMP_DIR") is { Length: > 0 } configured
                ? configured
                : Path.Combine(Path.GetTempPath(), "shadowdusk-hang-dumps"));
        Directory.CreateDirectory(dir);

        var report = new StringBuilder();
        report.AppendLine($"== {label}: hung child pid {pid}");

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

        string core = Path.Combine(dir, $"child-{pid}.dmp");
        if (OperatingSystem.IsWindows())
        {
            report.AppendLine(await WindowsMiniDump.WriteAsync(pid, core, ToolTimeout));
        }
        else
        {
            // createdump ships beside the .NET runtime. It only understands a .NET process;
            // pointed at a native one it fails, and the report says so.
            string createdump = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "createdump");
            report.AppendLine(File.Exists(createdump)
                ? await RunToolAsync(createdump, "-f", core, pid.ToString())
                : "createdump: not found next to the runtime");
        }

        string text = report.ToString();
        string reportPath = Path.Combine(dir, $"child-{pid}-stacks.txt");
        await File.WriteAllTextAsync(reportPath, text);
        return $"(native evidence: {reportPath}; dump: {core})\n{text}";
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

    /// <summary>
    /// A dump of another process through dbghelp, which every Windows install carries. The
    /// type is the "heap" set <c>dotnet-dump collect</c> uses, so <c>dotnet-dump analyze</c>
    /// can walk the managed stacks (<c>clrstack -all</c>) and a native debugger the rest.
    /// </summary>
    private static class WindowsMiniDump
    {
        // MiniDumpWithDataSegs | WithHandleData | WithUnloadedModules | WithPrivateReadWriteMemory
        // | WithFullMemoryInfo | WithThreadInfo | WithTokenInformation.
        private const int HeapDumpType = 0x1 | 0x4 | 0x20 | 0x200 | 0x800 | 0x1000 | 0x40000;

        // "All DbgHelp functions are single threaded": two tests can time out at once.
        private static readonly SemaphoreSlim DbgHelp = new(1, 1);

        public static async Task<string> WriteAsync(int pid, string path, TimeSpan within)
        {
            await DbgHelp.WaitAsync();
            try
            {
                // The native call cannot be interrupted, so it runs off the caller's thread and
                // the caller stops waiting at the bound; an abandoned call still finishes.
                return await Task.Run(() => Write(pid, path)).WaitAsync(within);
            }
            catch (TimeoutException)
            {
                return $"MiniDumpWriteDump: still writing after {within.TotalSeconds:0} s";
            }
            finally
            {
                DbgHelp.Release();
            }
        }

        private static string Write(int pid, string path)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                using FileStream file = File.Create(path);
                return MiniDumpWriteDump(process.Handle, pid, file.SafeFileHandle, HeapDumpType, 0, 0, 0)
                    ? $"--- MiniDumpWriteDump: wrote {file.Length} bytes"
                    : $"MiniDumpWriteDump: failed (Win32 error {Marshal.GetLastPInvokeError()})";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception
                or IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
            {
                return $"MiniDumpWriteDump: could not run ({ex.Message})";
            }
        }

        [DllImport("dbghelp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MiniDumpWriteDump(
            nint process, int processId, SafeHandle file, int dumpType,
            nint exceptionParam, nint userStreamParam, nint callbackParam);
    }

    private static async Task<string> RunToolAsync(string tool, params string[] arguments)
    {
        var psi = new ProcessStartInfo(tool);
        foreach (string argument in arguments) psi.ArgumentList.Add(argument);

        string name = Path.GetFileName(tool);
        try
        {
            ChildProcessResult result = await ChildProcess.RunAsync(psi, ToolTimeout, name);
            return $"--- {name} (exit {result.ExitCode})\n{result.Output}";
        }
        catch (ChildProcessTimeoutException ex)
        {
            // A debugger that is itself stuck still printed the threads it got to.
            return $"{name}: timed out after {ToolTimeout.TotalSeconds:0} s\n{ex.Stdout}{ex.Stderr}";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return $"{name}: could not run ({ex.Message})";
        }
    }
}
