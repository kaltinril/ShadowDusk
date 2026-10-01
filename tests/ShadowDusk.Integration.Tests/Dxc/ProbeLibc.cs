#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// The libc calls <see cref="DxcConcurrencyProbe"/> needs (macOS and glibc Linux only), made
/// through unmanaged function pointers resolved from the process's global symbol scope.
/// Function pointers, not delegates: the child of <see cref="ForkChildThatExits"/> must not
/// reach a marshalling stub or the JIT, and these call sites are compiled before any fork.
/// </summary>
internal static unsafe class ProbeLibc
{
    private static nint Export(string name) =>
        NativeLibrary.GetExport(NativeLibrary.GetMainProgramHandle(), name);

    /// <summary><c>LC_ALL</c>: 0 on macOS, 6 on glibc.</summary>
    private static int LcAll => OperatingSystem.IsMacOS() ? 0 : 6;

    /// <summary>Sets the process-wide C locale (<c>setlocale(LC_ALL, name)</c>); false on failure.</summary>
    public static bool SetLocale(string name)
    {
        var setlocale = (delegate* unmanaged<int, byte*, byte*>)Export("setlocale");
        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes)
            return setlocale(LcAll, p) != null;
    }

    /// <summary>The current process-wide C locale name (<c>setlocale(LC_ALL, NULL)</c>).</summary>
    public static string CurrentLocale()
    {
        var setlocale = (delegate* unmanaged<int, byte*, byte*>)Export("setlocale");
        byte* name = setlocale(LcAll, null);
        return name == null ? "<null>" : Marshal.PtrToStringUTF8((nint)name) ?? "<null>";
    }

    private static int _atforkPrepareCalls;

    /// <summary>How many times the counting <c>pthread_atfork</c> prepare handler has run.</summary>
    public static int AtforkPrepareCalls => Volatile.Read(ref _atforkPrepareCalls);

    /// <summary>
    /// Registers a <c>pthread_atfork</c> prepare handler that counts every <c>fork()</c> this
    /// process makes. A <c>vfork()</c>/<c>posix_spawn</c> never runs atfork handlers, so the
    /// count tells which one a code path uses.
    /// </summary>
    public static void RegisterAtforkCounter()
    {
        var pthreadAtfork = (delegate* unmanaged<delegate* unmanaged<void>, delegate* unmanaged<void>, delegate* unmanaged<void>, int>)
            Export("pthread_atfork");
        int rc = pthreadAtfork(&CountPrepare, null, null);
        if (rc != 0) throw new InvalidOperationException($"pthread_atfork failed: {rc}");
    }

    [UnmanagedCallersOnly]
    private static void CountPrepare() => Interlocked.Increment(ref _atforkPrepareCalls);

    /// <summary>
    /// A real libc <c>fork()</c> whose child immediately <c>_exit(0)</c>s. Returns the child's
    /// pid in the parent, or -1 if <c>fork()</c> failed. Kept tiny and non-inlined so the
    /// child runs nothing but the two calls.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ForkChildThatExits(nint fork, nint exit)
    {
        int pid = ((delegate* unmanaged<int>)fork)();
        if (pid == 0) ((delegate* unmanaged<int, void>)exit)(0);
        return pid;
    }

    /// <summary>The <c>fork</c> and <c>_exit</c> entry points for <see cref="ForkChildThatExits"/>.</summary>
    public static (nint Fork, nint Exit) ForkExports() => (Export("fork"), Export("_exit"));

    /// <summary>
    /// Reaps <paramref name="pid"/> with a blocking <c>waitpid</c>. Returns true when the
    /// child exited normally, false when it had to be killed or died by a signal.
    /// </summary>
    /// <remarks>
    /// A child of a <c>fork()</c> from a .NET process can stall forever if a GC suspension
    /// was in progress in the parent at the instant of the fork (the child's return to managed
    /// code waits for a GC that no thread in the child will finish). That is a child-side
    /// artifact, not the parent-side deadlock under test, so a child still alive after
    /// <paramref name="childTimeout"/> is killed and reported instead of hanging the probe.
    /// </remarks>
    public static bool Reap(int pid, TimeSpan childTimeout)
    {
        var waitpid = (delegate* unmanaged<int, int*, int, int>)Export("waitpid");
        var kill = (delegate* unmanaged<int, int, int>)Export("kill");
        using var killer = new Timer(_ => kill(pid, 9), null, childTimeout, Timeout.InfiniteTimeSpan);

        int status = 0;
        for (int attempt = 0; ; attempt++)
        {
            int rc = waitpid(pid, &status, 0);
            if (rc == pid) break;

            // -1 with EINTR (a signal landed while waiting) just retries; anything persistent
            // is a probe defect, not a finding.
            if (attempt >= 1000)
                throw new InvalidOperationException($"waitpid({pid}) kept failing: errno {Marshal.GetLastSystemError()}");
        }

        // WIFEXITED && WEXITSTATUS == 0 (same encoding on glibc and macOS).
        return (status & 0x7f) == 0 && ((status >> 8) & 0xff) == 0;
    }
}
