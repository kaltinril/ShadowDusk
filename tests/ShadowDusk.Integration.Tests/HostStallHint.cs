#nullable enable

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Issue #312: a hint from INSIDE a stalled test host that costs nothing and needs nothing.
/// Once the process has been up for three minutes (a healthy run of this assembly finishes in
/// well under that, so the file only exists for a slow or stalled run) a background thread
/// writes, once a minute, what this process can see about itself: thread and thread-pool
/// counts, every thread's CPU time, and the state of <c>DxcForkGate</c>'s
/// <see cref="ReaderWriterLockSlim"/> (how many compiles hold it, whether a <c>fork()</c> is
/// waiting to take it). No process is spawned and no dump is taken: on macOS a
/// <c>fork()</c> from a host whose fork gate is held by a stuck compile would itself block,
/// which is exactly why <c>--blame-hang-timeout</c> leaves no dump for this stall shape.
/// Which tests are in flight is already in the runner log, from xUnit's
/// <c>longRunningTestSeconds</c>, so it is not repeated here.
/// </summary>
/// <remarks>
/// Goes to <c>SHADOWDUSK_HANG_DUMP_DIR</c> (CI uploads that directory with the hang dumps),
/// else the temp directory. Started by a module initializer, so it also runs in the probe
/// children that re-run this assembly; they exit long before the first write.
/// </remarks>
internal static class HostStallHint
{
    private static readonly TimeSpan FirstWriteAfter = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    [ModuleInitializer]
    internal static void Start()
    {
        if (Environment.GetEnvironmentVariable("SHADOWDUSK_NO_STALL_HINT") == "1")
            return;

        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ShadowDusk stall hint",
        };
        thread.Start();
    }

    private static void Run()
    {
        var clock = Stopwatch.StartNew();
        Thread.Sleep(FirstWriteAfter);

        string dir = Environment.GetEnvironmentVariable("SHADOWDUSK_HANG_DUMP_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "shadowdusk-hang-dumps");
        string path = Path.Combine(
            dir, $"host-hint-{typeof(HostStallHint).Assembly.GetName().Name}-{Environment.ProcessId}.txt");

        while (true)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(path, Snapshot(clock.Elapsed));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere to write; the hint is best-effort.
            }
            Thread.Sleep(Interval);
        }
    }

    internal static string Snapshot(TimeSpan uptime)
    {
        var sb = new StringBuilder();
        sb.Append("== ").Append(DateTime.UtcNow.ToString("O")).Append(" pid ").Append(Environment.ProcessId)
          .Append(" up ").Append(uptime.ToString(@"mm\:ss")).AppendLine();
        sb.Append("thread pool: ").Append(ThreadPool.ThreadCount).Append(" threads, ")
          .Append(ThreadPool.PendingWorkItemCount).Append(" queued work items; ")
          .Append(Environment.ProcessorCount).AppendLine(" logical cores");
        sb.AppendLine(ForkGateState());

        try
        {
            using Process self = Process.GetCurrentProcess();
            sb.Append("process: CPU ").Append(self.TotalProcessorTime.TotalSeconds.ToString("0.##"))
              .Append(" s, ").Append(self.Threads.Count).AppendLine(" threads (id: CPU s, state)");
            foreach (ProcessThread thread in self.Threads.Cast<ProcessThread>().Take(128))
            {
                sb.Append("  ").Append(thread.Id).Append(": ");
                try
                {
                    sb.Append(thread.TotalProcessorTime.TotalSeconds.ToString("0.##")).Append(" s");
                }
                catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
                {
                    sb.Append("cpu ?");
                }
                try
                {
                    sb.Append(", ").Append(thread.ThreadState);
                    if (thread.ThreadState == System.Diagnostics.ThreadState.Wait)
                        sb.Append('/').Append(thread.WaitReason);
                }
                catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
                {
                    sb.Append(", state ?");
                }
                sb.AppendLine();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            sb.Append("process threads: unreadable (").Append(ex.Message).AppendLine(")");
        }

        return sb.ToString();
    }

    /// <summary>
    /// <c>DxcForkGate</c>'s lock, read by reflection so this file can be linked into any test
    /// assembly. Readers are compiles inside the native DXC call; a waiting writer is a
    /// <c>fork()</c> (a <c>Process.Start</c>, or the runtime's own createdump) blocked in the
    /// atfork prepare handler until every reader leaves.
    /// </summary>
    private static string ForkGateState()
    {
        Type? gateType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("ShadowDusk.HLSL.Dxc.DxcForkGate", throwOnError: false))
            .FirstOrDefault(t => t is not null);
        if (gateType is null)
            return "DxcForkGate: ShadowDusk.HLSL not loaded";

        const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        if (gateType.GetField("Gate", Static)?.GetValue(null) is not ReaderWriterLockSlim gate)
            return "DxcForkGate: its Gate field is not a ReaderWriterLockSlim any more; update HostStallHint";

        bool? active = gateType.GetField("_active", Static)?.GetValue(null) as bool?;
        bool? attempted = gateType.GetField("_installAttempted", Static)?.GetValue(null) as bool?;
        return $"DxcForkGate: installed={attempted?.ToString() ?? "?"} active={active?.ToString() ?? "?"} " +
               $"readers(compiles in the native call)={gate.CurrentReadCount} " +
               $"waitingWriters(forks blocked in the atfork handler)={gate.WaitingWriteCount} " +
               $"waitingReaders(compiles blocked behind a fork)={gate.WaitingReadCount} " +
               $"writeHeld={gate.IsWriteLockHeld}";
    }
}
