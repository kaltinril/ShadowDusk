#nullable enable

using System.Runtime.InteropServices;

namespace ShadowDusk.GLSL.Interop;

internal static class SpvcLoader
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.CompareExchange(ref _registered, 1, 0) != 0) return;
        NativeLibrary.SetDllImportResolver(
            typeof(SpvcLoader).Assembly,
            (name, _, _) =>
            {
                if (name != "spirv-cross-c-shared") return IntPtr.Zero;

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
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "spirv-cross-c-shared.dll"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)   ? "libspirv-cross-c-shared.dylib"
        :                                                       "libspirv-cross-c-shared.so";
}
