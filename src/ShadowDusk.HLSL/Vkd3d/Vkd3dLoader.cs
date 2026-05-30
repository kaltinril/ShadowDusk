#nullable enable

using System.Runtime.InteropServices;

namespace ShadowDusk.HLSL.Vkd3d;

/// <summary>
/// Resolves the native vkd3d-shader library per OS, mirroring
/// <c>ShadowDusk.GLSL.Interop.SpvcLoader</c>.
///
/// Unlike SPIRV-Cross (delivered as a NuGet that self-places under
/// <c>runtimes/&lt;rid&gt;/native</c>), the vkd3d-shader binary is a restored
/// artifact under <c>tools/vkd3d/</c> (see tools/restore.ps1). So the resolver
/// probes, in order:
///   1. the app base directory (where the .csproj copies it next to the binaries),
///   2. a <c>tools/vkd3d/</c> folder found by walking up from the base directory
///      (covers running straight out of bin/ during dev/tests),
///   3. a bare load by file name (single-file publish extracts natives to a temp
///      dir already on the native search path; also lets the OS loader use PATH).
///
/// Per-OS file names: Windows <c>libvkd3d-shader-1.dll</c>; Linux
/// <c>libvkd3d-shader.so.1</c> (then <c>.so</c>); macOS
/// <c>libvkd3d-shader.dylib</c>. Only the Windows binary exists locally today;
/// the others are named for the cross-platform path (Phase 18 follow-up).
/// </summary>
internal static class Vkd3dLoader
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.CompareExchange(ref _registered, 1, 0) != 0) return;

        NativeLibrary.SetDllImportResolver(
            typeof(Vkd3dLoader).Assembly,
            (name, _, _) =>
            {
                if (name != Vkd3dNative.LibName) return IntPtr.Zero;

                foreach (string fileName in GetLibFileNames())
                {
                    // 1. Next to the app binaries (csproj copy step).
                    string baseCandidate = Path.Combine(AppContext.BaseDirectory, fileName);
                    if (NativeLibrary.TryLoad(baseCandidate, out var handle))
                        return handle;

                    // 2. A tools/vkd3d folder above the base directory (dev/test runs).
                    string? toolsCandidate = FindToolsVkd3d(fileName);
                    if (toolsCandidate is not null && NativeLibrary.TryLoad(toolsCandidate, out handle))
                        return handle;

                    // 3. Bare name (single-file publish temp dir / OS search path).
                    if (NativeLibrary.TryLoad(fileName, out handle))
                        return handle;
                }

                return IntPtr.Zero;
            });
    }

    private static string? FindToolsVkd3d(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tools", "vkd3d", fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string[] GetLibFileNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ["libvkd3d-shader-1.dll"];
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ["libvkd3d-shader.1.dylib", "libvkd3d-shader.dylib"];
        return ["libvkd3d-shader.so.1", "libvkd3d-shader.so"];
    }
}
