#nullable enable

using System.Runtime.InteropServices;

namespace ShadowDusk.GLSL.Interop;

internal static class SpvcLoader
{
    private static readonly object RegisterGate = new();
    private static volatile bool _registered;

    // A lock (not a lone CAS) so a concurrent second caller BLOCKS until the winner
    // has finished installing the resolver — with CAS-then-subscribe the loser could
    // return and P/Invoke before the resolver existed (the DxcLoader race class).
    public static void Register()
    {
        if (_registered) return;
        lock (RegisterGate)
        {
            if (_registered) return;
            RegisterCore();
            _registered = true;
        }
    }

    private static void RegisterCore()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(SpvcLoader).Assembly,
            (name, _, _) =>
            {
                // Must match the DllImport name in SpvcNative (`LibName = "spirv-cross"`).
                // This previously tested for "spirv-cross-c-shared", which no P/Invoke
                // declares — the resolver was dead code and SPIRV-Cross loaded purely
                // via .NET default probing of the Silk.NET-shipped file names. The
                // resolver is a FALLBACK for layouts default probing misses (e.g. the
                // package runtimes/<rid>/native dir under the app base).
                if (name != SpvcNative.LibName) return IntPtr.Zero;

                var rid = GetCurrentRid();
                var candidate = Path.Combine(
                    AppContext.BaseDirectory,
                    "runtimes", rid, "native",
                    GetLibFileName());

                if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;

                // In single-file published executables the native libraries are extracted to
                // a temp directory that the host adds to the native search path, so a bare
                // TryLoad succeeds without needing the full path. On Android (Phase 50) the
                // runtimes/<rid>/native probe above never hits — the native rides in the APK's
                // per-ABI lib/<abi>/ dir, which the Android dynamic linker resolves by this
                // bare SONAME load (and W^X-safe: no temp-dir extraction is involved).
                NativeLibrary.TryLoad(GetLibFileName(), out handle);
                return handle;
            });
    }

    // ProcessArchitecture, not OSArchitecture: the native must match the PROCESS
    // (under Rosetta 2 the OS is Arm64 but the process loads only x64 dylibs).
    private static string GetCurrentRid() =>
        MapRid(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
            OperatingSystem.IsAndroid(),
            RuntimeInformation.ProcessArchitecture);

    // Pure (no RuntimeInformation) so the RID mapping is unit-testable. The RID only labels
    // the runtimes/<rid>/native probe in RegisterCore; on Android (Phase 50) that probe never
    // hits — the .so lives in the APK's lib/<abi>/ dir, resolved by the bare-name fallback.
    internal static string MapRid(bool isWindows, bool isOsx, bool isAndroid, Architecture arch) =>
        (isWindows, isOsx, isAndroid, arch) switch
        {
            // Per-arch on every OS (bug-hunt 2026-07-27 N18): Silk.NET.SPIRV.Cross.Native
            // ships win-arm64 and linux-arm64 natives too, and collapsing them to x64 made
            // this fallback probe a binary the process can never load.
            (true,  _,    _,    Architecture.Arm64) => "win-arm64",
            (true,  _,    _,    _)                  => "win-x64",
            (_,     true, _,    Architecture.Arm64) => "osx-arm64",
            (_,     true, _,    _)                  => "osx-x64",
            // Android ABIs (arm64-v8a is the primary; armeabi-v7a / x86_64 are stretch).
            (_,     _,    true, Architecture.Arm64) => "android-arm64",
            (_,     _,    true, Architecture.X64)   => "android-x64",
            (_,     _,    true, _)                  => "android-arm",
            (_,     _,    _,    Architecture.Arm64) => "linux-arm64",
            _                                       => "linux-x64",
        };

    private static string GetLibFileName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "spirv-cross.dll"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)   ? "libspirv-cross.dylib"
        :                                                       "libspirv-cross.so"; // Linux + Android
}
