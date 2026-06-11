#nullable enable

using System.Reflection;
using System.Runtime.InteropServices;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Resolves the native DXC library (<c>libdxcompiler.dylib</c>) on macOS, where
/// Vortice.Dxc 3.3.4 ships no native at all (win-x64/win-arm64/linux-x64 only —
/// the Phase 37 Finding A product gap). The dylib we load is OUR OWN build of the
/// EXACT pinned DXC commit the Vortice native reports
/// (<c>e043f4a1286f4e1026222ab1bc94e25de8d0e959</c>, FileVersion 1.7.2212.40 — the
/// same pin as the DXC-&gt;WASM build), never a substitute compiler, so macOS
/// SPIR-V stays byte-identical to the other RIDs.
///
/// CRITICAL difference from <see cref="Vkd3d.Vkd3dLoader"/> /
/// <c>ShadowDusk.GLSL.Interop.SpvcLoader</c>: the <c>dxcompiler.dll</c> P/Invokes
/// live in the <b>Vortice.Dxc</b> assembly, and Vortice's <c>Dxc</c> static
/// constructor already calls <c>NativeLibrary.SetDllImportResolver</c> on that
/// assembly — a second <c>SetDllImportResolver</c> there throws
/// <see cref="InvalidOperationException"/>. Vortice instead exposes the public
/// <c>Dxc.ResolveLibrary</c> event, which its resolver consults BEFORE falling back
/// to default loading; Vortice's own built-in handler returns
/// <see cref="IntPtr.Zero"/> on macOS (it only knows win-* layouts and a dxil+
/// dxcompiler pair that macOS lacks), so a handler appended here is the correct,
/// conflict-free hook.
///
/// The dylib ships two ways: packed into the ShadowDusk.HLSL NuGet under
/// <c>runtimes/osx-{x64,arm64}/native</c> (when restored at pack time), and as a
/// restored artifact under <c>tools/dxc/osx-{x64,arm64}/</c> for repo builds (see
/// tools/restore.ps1). Both arches share one file name, so the restored/copied
/// layout is per-arch, exactly like vkd3d's. Probe order (mirrors Vkd3dLoader):
///   1. the app base directory (per-arch subdir, then flat — the .csproj copy links),
///      plus the self-contained-publish <c>runtimes/&lt;rid&gt;/native</c> layout,
///   2. a <c>tools/dxc/</c> folder found by walking up from the base directory
///      toward the repo root (dev/test runs straight out of bin/),
///   3. the host's native search directories (<c>NATIVE_DLL_SEARCH_DIRECTORIES</c>)
///      — how the NuGet <c>runtimes/&lt;rid&gt;/native</c> asset resolves for
///      framework-dependent consumers,
///   4. a bare load by file name (single-file publish extraction dir / OS paths).
///
/// Only active on macOS — <see cref="Register"/> is a no-op on Windows/Linux, where
/// the Vortice-shipped natives already resolve; zero behavior change there.
/// </summary>
internal static class DxcLoader
{
    /// <summary>The module name Vortice.Dxc's P/Invokes declare on every OS.</summary>
    internal const string DxcLibraryName = "dxcompiler.dll";

    /// <summary>The file name our macOS DXC build ships under (both arches).</summary>
    internal const string MacLibFileName = "libdxcompiler.dylib";

    private static int _registered;

    /// <summary>
    /// Idempotently hooks <c>Vortice.Dxc.Dxc.ResolveLibrary</c> on macOS. Must run
    /// before the first DXC P/Invoke (<see cref="DxcShaderCompiler"/>'s constructor
    /// and <c>DxilReflectionExtractor</c> call it first thing).
    /// </summary>
    public static void Register()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
        if (Interlocked.CompareExchange(ref _registered, 1, 0) != 0) return;

        // Touching the event runs Dxc's static ctor first, so Vortice's built-in
        // handler is always ahead of ours in the invocation list — it yields Zero
        // on macOS and we take over.
        Vortice.Dxc.Dxc.ResolveLibrary += Resolve;
    }

    private static IntPtr Resolve(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != DxcLibraryName) return IntPtr.Zero;

        IntPtr handle;
        foreach (string candidate in GetProbeCandidates(
                     AppContext.BaseDirectory, RuntimeInformation.OSArchitecture))
        {
            if (NativeLibrary.TryLoad(candidate, out handle))
                return handle;
        }

        // NuGet runtimes/<rid>/native asset for framework-dependent consumers (the
        // natives live in the package cache, never the app base directory).
        foreach (string dir in GetNativeSearchDirectories())
        {
            string candidate = Path.Combine(dir, MacLibFileName);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                return handle;
        }

        // Bare name (single-file publish temp dir / OS search path).
        if (NativeLibrary.TryLoad(MacLibFileName, out handle))
            return handle;

        return IntPtr.Zero;
    }

    /// <summary>
    /// The ordered, fully-qualified file-path candidates probed before the host's
    /// native search directories. Pure (no I/O) so the order is unit-testable:
    /// base-dir per-arch subdir, base-dir flat, the publish
    /// <c>runtimes/&lt;rid&gt;/native</c> layout, then <c>tools/dxc/</c> per-arch
    /// (and flat, for a manually-placed dylib) walking up to the filesystem root.
    /// </summary>
    internal static IEnumerable<string> GetProbeCandidates(
        string baseDirectory, Architecture osArchitecture)
    {
        string rid = osArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";

        // 1. Next to the app binaries (csproj copy links; per-arch first, then flat).
        yield return Path.Combine(baseDirectory, rid, MacLibFileName);
        yield return Path.Combine(baseDirectory, MacLibFileName);

        // Self-contained publish keeps the package layout under the app base.
        yield return Path.Combine(baseDirectory, "runtimes", rid, "native", MacLibFileName);

        // 2. tools/dxc/ above the base directory (dev/test runs out of bin/).
        for (DirectoryInfo? dir = new(baseDirectory); dir is not null; dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "tools", "dxc", rid, MacLibFileName);
            yield return Path.Combine(dir.FullName, "tools", "dxc", MacLibFileName);
        }
    }

    private static string[] GetNativeSearchDirectories()
    {
        // Set by the host from deps.json (includes each package's runtimes/<rid>/native).
        return AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string dirs
            ? dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            : [];
    }
}
