#nullable enable

using System.Runtime.InteropServices;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Keeps <c>fork()</c> (every <c>Process.Start</c>) from running while DXC is inside its native
/// compile on macOS, where the two deadlock inside libc.
/// </summary>
/// <remarks>
/// <para>
/// DXC's non-Windows <c>WideCharToMultiByte</c>/<c>MultiByteToWideChar</c> shims
/// (<c>lib/DxcSupport/Unicode.cpp</c>) call <c>setlocale(LC_ALL, ...)</c> twice per string
/// conversion, about 180 times per compile. A DXC thread in
/// <c>setlocale -&gt; loadlocale -&gt; malloc</c> blocks in <c>_xzm_fork_lock_wait</c> while a
/// forking thread blocks in <c>libSystem_atfork_prepare</c> on a lock that thread holds, and
/// neither ever wakes. One DXC thread and one thread starting processes is enough; unfixed,
/// about half of 15-second stress runs hung (macOS 26.5.1 arm64).
/// </para>
/// <para>
/// The fix is ours, not DXC's: a <c>pthread_atfork</c> prepare handler takes this gate
/// exclusively, so a fork waits for in-flight compiles and new compiles wait for the fork; the
/// parent handler releases it. Compiles only share the gate, so they still run in parallel. No
/// child handler: the child of a .NET <c>Process.Start</c> only calls <c>execve</c>, and
/// managed code must not run in a forked child.
/// </para>
/// <para>
/// The gate must cover ONLY the direct native <c>IDxcCompiler3::Compile</c> call. Every
/// <c>setlocale</c> DXC makes happens inside it (measured), and nothing else may run under the
/// gate that can <c>dlopen</c>/<c>dlsym</c>, a first-call P/Invoke bind included: the forking
/// thread already holds dyld's lock while it waits in the prepare handler, so that is a new
/// deadlock (observed when the gate briefly wrapped <c>DxcCreateInstance</c>).
/// </para>
/// <para>
/// macOS only: only macOS is measured to deadlock. Linux (glibc) is not measured; whether its
/// <c>fork()</c> and <c>setlocale</c> can invert the same way is unknown.
/// </para>
/// </remarks>
internal static unsafe class DxcForkGate
{
    private static readonly ReaderWriterLockSlim Gate = new(LockRecursionPolicy.SupportsRecursion);
    private static readonly object InstallLock = new();
    private static volatile bool _installAttempted;
    private static volatile bool _active;

    /// <summary>
    /// Enters the shared side of the gate around one native DXC compile call (a no-op off macOS,
    /// or if the fork handler could not be registered). Dispose to leave.
    /// </summary>
    public static Scope Enter()
    {
        if (!OperatingSystem.IsMacOS()) return default;
        if (!_installAttempted) Install();
        if (!_active) return default;

        Gate.EnterReadLock();
        return new Scope(entered: true);
    }

    private static void Install()
    {
        lock (InstallLock)
        {
            if (_installAttempted) return;
            try
            {
                var pthreadAtfork = (delegate* unmanaged<delegate* unmanaged<void>, delegate* unmanaged<void>, delegate* unmanaged<void>, int>)
                    NativeLibrary.GetExport(NativeLibrary.GetMainProgramHandle(), "pthread_atfork");
                _active = pthreadAtfork(&BeforeFork, &AfterForkInParent, null) == 0;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or NotSupportedException or InvalidOperationException)
            {
                // e.g. loaded into a collectible AssemblyLoadContext, where unmanaged entry points
                // are unavailable: no gate, the pre-existing behavior.
                _active = false;
            }

            _installAttempted = true;
        }
    }

    [UnmanagedCallersOnly]
    private static void BeforeFork()
    {
        // Never let an exception escape into fork(): an UnmanagedCallersOnly method that throws
        // fails the process. The only expected one is a fork started from inside a DXC call
        // (LockRecursionException); that fork proceeds ungated, as it did before.
        try { Gate.EnterWriteLock(); }
        catch { }
    }

    [UnmanagedCallersOnly]
    private static void AfterForkInParent()
    {
        try
        {
            if (Gate.IsWriteLockHeld) Gate.ExitWriteLock();
        }
        catch { }
    }

    /// <summary>Releases the shared side of the gate.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly bool _entered;

        internal Scope(bool entered) => _entered = entered;

        public void Dispose()
        {
            if (_entered) Gate.ExitReadLock();
        }
    }
}
