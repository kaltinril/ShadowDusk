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
                // TryLoad succeeds without needing the full path.
                NativeLibrary.TryLoad(GetLibFileName(), out handle);
                return handle;
            });
    }

    private static string GetCurrentRid() =>
        (RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
         RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
         RuntimeInformation.OSArchitecture) switch
        {
            (true,  _,    _)                  => "win-x64",
            (false, true, Architecture.Arm64) => "osx-arm64",
            (false, true, _)                  => "osx-x64",
            _                                 => "linux-x64",
        };

    private static string GetLibFileName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "spirv-cross.dll"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)   ? "libspirv-cross.dylib"
        :                                                       "libspirv-cross.so";
}
