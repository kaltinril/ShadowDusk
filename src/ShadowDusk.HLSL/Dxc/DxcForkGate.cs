#nullable enable

using System.Runtime.InteropServices;
using Vortice.Dxc;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Keeps <c>fork()</c> (every <c>Process.Start</c>) from running while DXC changes the
/// process's C locale on macOS, where the two deadlock inside libc, and makes sure that
/// change happens once, under the gate, rather than inside an ordinary compile.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hazard.</b> DXC's non-Windows <c>WideCharToMultiByte</c>/<c>MultiByteToWideChar</c>
/// shims (<c>lib/DxcSupport/Unicode.cpp</c>) call <c>setlocale(LC_ALL, "en_US.UTF-8")</c>
/// twice per string conversion, about 180 times per compile. Apple's <c>setlocale</c> holds
/// the global locale lock while <c>loadlocale</c> runs each category's loader, which
/// allocates; a thread allocating there while another thread is inside <c>fork()</c> blocks on
/// malloc's fork lock (<c>_xzm_fork_lock_wait</c>) while the forking thread, in
/// <c>libSystem_atfork_prepare</c>, waits for the locale lock that thread holds, and neither
/// ever wakes (measured on macOS 26 arm64; one DXC thread and one thread starting processes is
/// enough). The loaders only run when a category actually CHANGES:
/// <c>loadlocale</c> returns before them when the requested name equals the current one
/// (Apple Libc <c>locale/FreeBSD/setlocale.c</c>, <c>strcmp(new, old) == 0</c>). DXC's shims
/// never restore the previous locale (they save the return value of the SET call, which is
/// the new name), and every conversion on the compile path uses <c>CP_UTF8</c>, so after the
/// first conversion the process locale is DXC's UTF-8 locale for good and every later
/// <c>setlocale</c> DXC makes is a same-name call that allocates nothing. The deadlock
/// therefore needs the one call that changes the locale, and that is the only call this gate
/// has to cover.
/// </para>
/// <para>
/// <b>Why not gate every compile.</b> The first version of this gate held every native
/// <c>IDxcCompiler3::Compile</c> call as a reader and made <c>fork()</c> take the write side in
/// a <c>pthread_atfork</c> prepare handler. That deadlocked a different way (issue #312,
/// measured in CI with lldb stacks of the hung test host): libSystem runs
/// <c>_dyld_dlopen_atfork_prepare</c>, which takes dyld's dlopen lock, BEFORE the client
/// prepare handlers, so the forking thread waited for the compiles while holding that lock;
/// and a SPIR-V compile with debug information (<c>-Zi</c> on the OpenGL and Vulkan targets)
/// <c>dlopen</c>s <c>libdxcompiler</c> itself from inside the compile
/// (<c>clang::spirv::ReadSourceCode</c>, which loads the compiler through
/// <c>DxcDllSupport</c> to read the source file for <c>OpSource</c>; still so upstream), so
/// the reader waited for the lock the writer held. Any design in which a fork waits for a
/// compile that can <c>dlopen</c> has that cycle, so forks no longer wait for compiles at all.
/// </para>
/// <para>
/// <b>What is gated now.</b> <see cref="SettleLocale"/> runs before every compile: if the
/// process locale is not the one DXC leaves behind (the first compile in the process, or a
/// host that set its own locale since), it runs one trivial <c>-P</c> preprocess under the
/// gate, which performs DXC's locale change while no <c>fork()</c> can run, then records the
/// resulting locale. A <c>-P</c> call never reaches the SPIR-V emitter, so it cannot
/// <c>dlopen</c>, and the gate is sound for it. The signal-isolation prime
/// (<see cref="DxcSignalIsolation"/>) is the same kind of call and is gated the same way.
/// Ordinary compiles run with no gate: a same-name <c>setlocale</c> takes the locale lock and
/// returns, so a concurrent <c>fork()</c> waits at most for that, and a compile's
/// <c>dlopen</c> waits at most for the fork to finish.
/// </para>
/// <para>
/// A fork that starts between the check and DXC's own call can still see a locale change if
/// the HOST changed the locale in that window; that is the host's race, outside what this
/// library can serialize, and the check costs one <c>setlocale(LC_ALL, NULL)</c> per compile.
/// </para>
/// <para>
/// macOS only, deliberately (issue #256). On glibc Linux, measured on ubuntu CI: thousands of
/// DXC compiles racing both <c>Process.Start</c> and real libc <c>fork()</c> calls never hung.
/// That matches glibc's design: <c>Process.Start</c> uses <c>vfork()</c> there (measured: it
/// runs no atfork handler), which takes none of glibc's fork-time locks, and glibc's real
/// <c>fork()</c> takes the malloc arena and stdio-list locks but never the
/// <c>setlocale</c> lock, so a thread inside <c>setlocale</c> waits for <c>fork()</c> to
/// finish rather than the reverse. If it is ever enabled there, note that glibc does not export
/// <c>pthread_atfork</c> from <c>libc.so</c> (it is in <c>libc_nonshared.a</c>), so
/// <see cref="Install"/> would silently stay inactive; register through
/// <c>__register_atfork</c> instead.
/// </para>
/// </remarks>
internal static unsafe class DxcForkGate
{
    private static readonly ReaderWriterLockSlim Gate = new(LockRecursionPolicy.SupportsRecursion);
    private static readonly object InstallLock = new();
    private static readonly object SettleLock = new();
    private static volatile bool _installAttempted;
    private static volatile bool _active;

    /// <summary>The process locale DXC left behind after the last gated prime; null until one ran.</summary>
    private static volatile string? _settledLocale;

    private const int LcAll = 0; // macOS <locale.h>

    private const string PrimeSource = "float4 main() : SV_Target { return 0; }";

    /// <summary>
    /// Enters the shared side of the gate around one native DXC call that may change the
    /// process locale (a no-op off macOS, or if the fork handler could not be registered).
    /// Dispose to leave. Only for calls that cannot <c>dlopen</c>: a <c>-P</c> preprocess.
    /// </summary>
    public static Scope Enter()
    {
        if (!OperatingSystem.IsMacOS()) return default;
        if (!_installAttempted) Install();
        if (!_active) return default;

        Gate.EnterReadLock();
        return new Scope(entered: true);
    }

    /// <summary>
    /// Makes sure the process locale is already the one DXC's shims set, so that the compile
    /// that follows makes only same-name <c>setlocale</c> calls. When it is not (first use, or
    /// the host changed the locale), performs DXC's locale change with one gated <c>-P</c>
    /// preprocess. A no-op off macOS.
    /// </summary>
    public static void SettleLocale(IDxcCompiler3 compiler)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (_settledLocale is { } settled && settled == CurrentLocale()) return;

        lock (SettleLock)
        {
            if (_settledLocale is { } again && again == CurrentLocale()) return;

            using IDxcResult result = DxcNativeInterop.CompileRaw(
                compiler, PrimeSource, DxcFlagBuilder.BuildPreprocess([]), includeHandler: null, forkGated: true);

            _settledLocale = CurrentLocale();
        }
    }

    /// <summary>The current process-wide C locale name (<c>setlocale(LC_ALL, NULL)</c>), macOS only.</summary>
    internal static string CurrentLocale()
    {
        var setlocale = (delegate* unmanaged<int, byte*, byte*>)NativeLibrary.GetExport(
            NativeLibrary.GetMainProgramHandle(), "setlocale");
        byte* name = setlocale(LcAll, null);
        return name == null ? "" : Marshal.PtrToStringUTF8((nint)name) ?? "";
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
