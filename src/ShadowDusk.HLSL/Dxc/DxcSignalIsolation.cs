#nullable enable

using System.Runtime.InteropServices;
using Vortice.Dxc;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Keeps DXC's LLVM signal handlers out of the .NET process on macOS, Linux, and Android.
/// </summary>
/// <remarks>
/// <para>
/// DXC's non-Windows builds compile LLVM 3.7's <c>lib/Support/Unix/Signals.inc</c>. Every DXIL
/// compile and every <c>-P</c> preprocess reaches <c>llvm::sys::RemoveFileOnSignal</c> (clang's
/// <c>CompilerInstance::createOutputFile</c> always asks for it, and no DXC argument turns it
/// off), which calls <c>RegisterHandlers()</c>. That installs LLVM's own handler, with
/// <c>SA_RESETHAND</c>, over 17 process-wide signals, including the ones the .NET runtime
/// itself depends on: <c>SIGSEGV</c>/<c>SIGBUS</c>/<c>SIGFPE</c> (hardware exceptions) and, on
/// macOS, <c>SIGUSR1</c>, which CoreCLR uses to interrupt threads for GC suspension. Two
/// process-killing failures follow, both observed in the integration suite on macOS arm64:
/// </para>
/// <list type="bullet">
/// <item><c>RegisterHandlers()</c> runs outside LLVM's <c>SignalsMutex</c> and does an
/// unsynchronized check-then-append into a fixed 17-slot array. Two threads making their
/// first DXIL/preprocess call together both register, overflowing the array into the
/// adjacent <c>TargetRegistry</c> list head, so the next DXIL compile calls through a saved
/// signal handler's bytes: SIGSEGV/SIGBUS in <c>llvm::TargetRegistry::lookupTarget</c>, or a
/// process spinning forever re-faulting through LLVM's handler.</item>
/// <item>The test host was killed by <c>SIGUSR1</c> (exit code 158) while LLVM's handler sat on
/// it. Likely mechanism (inferred, not traced): <c>SA_RESETHAND</c> resets the disposition to
/// <c>SIG_DFL</c> on the first delivery, and CoreCLR signals several threads at once to suspend
/// them, so the next delivery runs the default action.</item>
/// </list>
/// <para>
/// The fix sits on our side of the boundary, without patching DXC: before the first real
/// call, run ONE trivial preprocess under a lock, which performs LLVM's registration, then put
/// back the handlers that were installed before it. <c>RegisterHandlers()</c> returns early
/// whenever its registered count is non-zero, and only LLVM's own handler (now no longer
/// installed) ever resets that count, so LLVM never touches the process's signal state again
/// and later compiles run fully in parallel. Windows DXC uses <c>Windows/Signals.inc</c> and is
/// untouched. If code outside ShadowDusk already ran DXC in this process, the "before" state
/// already holds LLVM's handlers and this cannot help; ShadowDusk only guarantees its own use.
/// </para>
/// </remarks>
internal static unsafe class DxcSignalIsolation
{
    // LLVM 3.7 Unix Signals.inc: IntSigs { SIGHUP, SIGINT, SIGPIPE, SIGTERM, SIGUSR1, SIGUSR2 }
    // + KillSigs { SIGILL, SIGTRAP, SIGABRT, SIGFPE, SIGBUS, SIGSEGV, SIGQUIT, SIGSYS, SIGXCPU,
    // SIGXFSZ[, SIGEMT] }, in each OS's numbering. Only these are restored, so a .NET handler
    // installed lazily on some other signal (SIGCHLD for Process, ...) is never reverted.
    private static readonly int[] MacOsSignals =
        [1, 2, 13, 15, 30, 31, 4, 5, 6, 8, 10, 11, 3, 12, 24, 25, 7];

    private static readonly int[] LinuxSignals =
        [1, 2, 13, 15, 10, 12, 4, 5, 6, 8, 7, 11, 3, 31, 24, 25];

    /// <summary>
    /// Opaque <c>struct sigaction</c> buffer size: larger than every libc's layout (glibc 152
    /// bytes, macOS 16, bionic smaller), so a get-then-set round trip never truncates.
    /// </summary>
    private const int SigactionBufferSize = 512;

    private const string PrimeSource = "float4 main() : SV_Target { return 0; }";

    private static readonly object Gate = new();
    private static volatile bool _isolated;

    /// <summary>The signals LLVM's handler registration touches on this OS (empty on Windows).</summary>
    internal static IReadOnlyList<int> LlvmSignals =>
        OperatingSystem.IsMacOS() ? MacOsSignals
        : OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() ? LinuxSignals
        : [];

    /// <summary>
    /// Idempotent. On the first call on a non-Windows host, performs DXC's signal registration
    /// once (serialized) and restores the pre-existing handlers; a no-op afterwards and on
    /// Windows or the browser.
    /// </summary>
    public static void EnsureIsolated(IDxcCompiler3 compiler)
    {
        if (_isolated) return;
        if (LlvmSignals.Count == 0)
        {
            _isolated = true;
            return;
        }

        lock (Gate)
        {
            if (_isolated) return;

            var sigaction = (delegate* unmanaged<int, byte*, byte*, int>)NativeLibrary.GetExport(
                NativeLibrary.GetMainProgramHandle(), "sigaction");

            IReadOnlyList<int> signals = LlvmSignals;
            byte[] before = new byte[signals.Count * SigactionBufferSize];
            byte[] after = new byte[SigactionBufferSize];

            fixed (byte* pBefore = before)
            {
                for (int i = 0; i < signals.Count; i++)
                    sigaction(signals[i], null, pBefore + (i * SigactionBufferSize));

                try
                {
                    // Any -P preprocess reaches RemoveFileOnSignal -> RegisterHandlers. It is
                    // also the first DXC call in the process, so it performs DXC's one-time
                    // locale change: under the fork gate (see DxcForkGate), which is sound for
                    // a preprocess because it never reaches the SPIR-V emitter's dlopen.
                    using IDxcResult result = DxcNativeInterop.CompileRaw(
                        compiler,
                        PrimeSource,
                        DxcFlagBuilder.BuildPreprocess([]),
                        includeHandler: null,
                        forkGated: true);
                }
                finally
                {
                    fixed (byte* pAfter = after)
                    {
                        for (int i = 0; i < signals.Count; i++)
                        {
                            byte* saved = pBefore + (i * SigactionBufferSize);
                            new Span<byte>(pAfter, SigactionBufferSize).Clear();
                            sigaction(signals[i], null, pAfter);
                            if (!new ReadOnlySpan<byte>(saved, SigactionBufferSize)
                                    .SequenceEqual(new ReadOnlySpan<byte>(pAfter, SigactionBufferSize)))
                            {
                                sigaction(signals[i], saved, null);
                            }
                        }
                    }
                }
            }

            // Only after a prime that returned: if it threw, the next call retries it.
            _isolated = true;
        }
    }
}
