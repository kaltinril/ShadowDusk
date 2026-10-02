#nullable enable

using System.Reflection;
using System.Runtime.InteropServices;
using ShadowDusk.Core;

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
/// Active on macOS and, since Phase 50, Android (Vortice ships no android RID either, so
/// we load our own <c>libdxcompiler.so</c> by bare SONAME from the APK's per-ABI
/// <c>lib/&lt;abi&gt;/</c> dir — never the desktop path-probing, which would violate
/// Android W^X).
///
/// <para><b>Windows and Linux: the pinned pair, by absolute path, before anything else
/// (issue: DXC/dxil PATH hijack, 2026-10-01).</b> Vortice ships the natives there, but loading
/// them is not left to name-based search any more. <c>dxcompiler.dll</c> binds its DXIL
/// validator/signer from its own <c>DllMain</c> with a bare <c>LoadLibrary("dxil.dll")</c>,
/// and a bare name resolves to an already-loaded module of that name first, then the
/// application directory, the system directories and finally <c>PATH</c>. ShadowDusk used to
/// call Vortice's <c>Dxc.LoadDxil()</c> (itself a bare <c>LoadLibrary("dxil.dll")</c>) before
/// the first DXC P/Invoke; in the normal layout our <c>dxil.dll</c> sits in
/// <c>runtimes/&lt;rid&gt;/native</c>, not the application directory, so that call loaded
/// whichever <c>dxil.dll</c> the OS search found on <c>PATH</c> (every VS Developer Command
/// Prompt puts the Windows SDK's 1.8 one there) and DXC then validated with it: every
/// DirectX 12 compile failed with "DXIL container mismatch for 'PSVRuntimeInfoSize'", and a
/// loadable non-validator decoy silently produced UNSIGNED DXIL. <see cref="Register"/> now
/// locates the pinned pair (<see cref="GetPinnedPairDirectories"/>), loads <c>dxil.dll</c>
/// then <c>dxcompiler.dll</c> by full path, answers Vortice's resolver with that handle, and
/// verifies the <c>dxil.dll</c> a bare-name lookup returns is ours. If the pair is missing,
/// or a foreign <c>dxil.dll</c> was loaded into the process first, it reports
/// <see cref="LoadErrorCode"/> instead of compiling with whatever the OS search offers.
/// (Linux's <c>libdxcompiler.so</c> at this pin never loads <c>libdxil.so</c>, so there the
/// risk was only a foreign <c>libdxcompiler.so</c> via <c>LD_LIBRARY_PATH</c>, closed the
/// same way.)</para>
///
/// <para><b>macOS</b> ships no <c>libdxil.dylib</c>, but our <c>libdxcompiler.dylib</c> still
/// <c>dlopen</c>s one by leaf name, which dyld resolves through <c>DYLD_LIBRARY_PATH</c>, the
/// working directory and <c>/usr/local/lib</c>. We cannot pre-empt a library we do not ship,
/// so <see cref="CheckBoundValidator"/> fails loudly if any <c>libdxil</c> image is loaded.</para>
/// </summary>
internal static class DxcLoader
{
    /// <summary>The module name Vortice.Dxc's P/Invokes declare on every OS.</summary>
    internal const string DxcLibraryName = "dxcompiler.dll";

    /// <summary>The file name our macOS DXC build ships under (both arches).</summary>
    internal const string MacLibFileName = "libdxcompiler.dylib";

    /// <summary>
    /// The file name our Android DXC build ships under (Phase 50). It rides in the APK's
    /// per-ABI <c>lib/&lt;abi&gt;/</c> dir and the Android dynamic linker resolves it by this
    /// SONAME — a bare-name load, never the desktop path-probing (which would violate
    /// Android W^X and target the sandboxed app-data dir, not the read-only APK).
    /// </summary>
    internal const string AndroidLibFileName = "libdxcompiler.so";

    /// <summary>
    /// The diagnostic raised when ShadowDusk cannot guarantee DXC runs with its own pinned
    /// natives: the pair is missing or unloadable, or a foreign DXIL validator
    /// (<c>dxil.dll</c> / <c>libdxil</c>) is the one DXC binds.
    /// </summary>
    internal const string LoadErrorCode = "SD0219";

    private static readonly object RegisterGate = new();
    private static volatile bool _registered;
    private static ShaderError? _loadError;
    private static IntPtr _pinnedDxcHandle;

    /// <summary>
    /// Idempotently makes DXC resolvable as ShadowDusk's own pinned build. Must run before the
    /// first DXC P/Invoke (<see cref="DxcShaderCompiler"/>'s constructor and
    /// <c>DxilReflectionExtractor</c> call it first thing).
    /// <list type="bullet">
    /// <item>Windows/Linux: loads the pinned pair by absolute path (<c>dxil</c> first, so
    ///   <c>dxcompiler</c>'s own bare-name validator load finds it) and answers
    ///   <c>Dxc.ResolveLibrary</c> with that handle.</item>
    /// <item>macOS/Android: hooks <c>Dxc.ResolveLibrary</c> for our own <c>libdxcompiler</c>
    ///   (Vortice ships none there).</item>
    /// </list>
    /// A lock (not a lone CAS) so a concurrent second caller BLOCKS until the
    /// winner has finished — with CAS-then-subscribe the loser could
    /// return and P/Invoke before the resolver existed (observed once as an
    /// intermittent macOS <c>DllNotFoundException</c> under test parallelism).
    /// </summary>
    /// <returns>
    /// <c>null</c> when DXC may be used; otherwise a <see cref="LoadErrorCode"/> error the
    /// caller must surface instead of compiling. The outcome is computed once per process.
    /// </returns>
    public static ShaderError? Register()
    {
        if (_registered) return _loadError;
        lock (RegisterGate)
        {
            if (_registered) return _loadError;

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsAndroid())
            {
                // Touching the event runs Dxc's static ctor first, so Vortice's built-in
                // handler is always ahead of ours in the invocation list — it yields Zero
                // on macOS and we take over.
                Vortice.Dxc.Dxc.ResolveLibrary += Resolve;
            }
            else if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                _loadError = LoadPinnedPair();
                if (_loadError is null)
                {
                    // Vortice's own handler stays first in the invocation list. It loads the
                    // same files (its probe is base/runtimes/<rid>/native, then the host's
                    // search directories, the same order GetPinnedPairDirectories starts
                    // with), and any bare-name load it falls back to returns the module we
                    // already loaded under that name.
                    Vortice.Dxc.Dxc.ResolveLibrary += ResolvePinned;
                }
            }

            _registered = true;
            return _loadError;
        }
    }

    /// <summary>
    /// Checks, after a native DXC call, that DXC is not validating with a foreign DXIL library.
    /// Windows binds <c>dxil.dll</c> when <c>dxcompiler.dll</c> loads, so <see cref="Register"/>
    /// already verified it once; macOS may bind <c>libdxil.dylib</c> lazily on the first DXIL
    /// compile and ships none of its own, so any loaded <c>libdxil</c> image is foreign.
    /// Returns <c>null</c> when clean, or when the check itself cannot run (never a false
    /// positive).
    /// </summary>
    internal static ShaderError? CheckBoundValidator()
    {
        if (!OperatingSystem.IsMacOS()) return null;

        string? foreign = MacDyld.FindLoadedImage(IsDxilLeafName);
        return foreign is null
            ? null
            : LoadError(
                $"A DXIL validator library ('{foreign}') is loaded into this process, and " +
                "ShadowDusk's macOS DXC ships none: DXC picked it up from the dynamic-linker search " +
                "path (DYLD_LIBRARY_PATH, the working directory, or /usr/local/lib) and would " +
                "validate DXIL with a foreign build. Remove that library from those paths.");
    }

    /// <summary>True for the leaf names DXC's non-Windows builds <c>dlopen</c> as their validator.</summary>
    internal static bool IsDxilLeafName(string fileName) =>
        fileName.Equals("libdxil.dylib", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("libdxil.so", StringComparison.OrdinalIgnoreCase);

    private static IntPtr ResolvePinned(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName == DxcLibraryName ? _pinnedDxcHandle : IntPtr.Zero;

    private static ShaderError LoadError(string message) => new(
        File: "",
        Line: 0,
        Column: 0,
        Code: LoadErrorCode,
        Message: message);

    /// <summary>
    /// Windows/Linux: finds the pinned pair, loads it by absolute path, and (Windows) verifies
    /// that a bare-name <c>dxil.dll</c> lookup, the one DXC's <c>DllMain</c> made, returns ours.
    /// </summary>
    private static ShaderError? LoadPinnedPair()
    {
        bool windows = OperatingSystem.IsWindows();
        string dxcFile = windows ? "dxcompiler.dll" : "libdxcompiler.so";
        string dxilFile = windows ? "dxil.dll" : "libdxil.so";
        string rid = PinnedRid(windows, RuntimeInformation.ProcessArchitecture);

        List<string> directories = GetPinnedPairDirectories(
            AppContext.BaseDirectory,
            GetNativeSearchDirectories(),
            GetAssemblyDirectories(),
            rid,
            windows).ToList();

        foreach (string directory in directories)
        {
            string dxcPath = Path.Combine(directory, dxcFile);
            string dxilPath = Path.Combine(directory, dxilFile);
            if (!File.Exists(dxcPath)) continue;

            bool hasDxil = File.Exists(dxilPath);

            // Windows: a dxcompiler.dll without its dxil.dll beside it would bind whatever
            // dxil.dll the OS search finds, so a half pair is not ours to use.
            if (windows && !hasDxil) continue;

            try
            {
                if (windows)
                    NativeLibrary.Load(dxilPath);
                else if (hasDxil)
                    NativeLibrary.TryLoad(dxilPath, out _); // unused by this pin's Linux DXC; loaded like Vortice does

                _pinnedDxcHandle = NativeLibrary.Load(dxcPath);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                return LoadError(
                    $"ShadowDusk's pinned DXC native library at '{dxcPath}' could not be loaded: {ex.Message}");
            }

            return windows ? VerifyBoundWindowsDxil(dxilPath) : null;
        }

        string pair = windows ? $"{dxilFile} + {dxcFile}" : dxcFile;
        return LoadError(
            $"ShadowDusk's pinned DXC native library ({pair}, from the Vortice.Dxc package's " +
            $"runtimes/{rid}/native) was not found, so no DXC-backed compile can run. ShadowDusk " +
            "will not fall back to a DXC found on the system search path (PATH), which would be a " +
            "different compiler. Searched: " + string.Join("; ", directories));
    }

    private static ShaderError? VerifyBoundWindowsDxil(string pinnedDxilPath)
    {
        string? bound = WindowsModules.GetLoadedModulePath("dxil.dll");
        if (bound is null || SamePath(bound, pinnedDxilPath))
            return null;

        return LoadError(
            $"A different dxil.dll ('{bound}') was loaded into this process before ShadowDusk's " +
            $"pinned one ('{pinnedDxilPath}'). DXC binds its DXIL validator and signer to the " +
            "dxil.dll already loaded under that name, so it would validate and sign with a " +
            "foreign build (a newer one rejects this DXC's output outright). Whatever loaded it " +
            "first must not share the process with ShadowDusk's compiler.");
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The directories probed, in order, for the pinned Windows/Linux pair. Pure (no I/O), so
    /// the order is unit-testable. The first two mirror Vortice.Dxc's own resolver, so the
    /// files we load are the files it would load:
    /// <list type="number">
    /// <item><c>&lt;base&gt;/runtimes/&lt;rid&gt;/native</c>: an ordinary framework-dependent
    ///   app, test host or dotnet tool.</item>
    /// <item>the host's native search directories (<c>NATIVE_DLL_SEARCH_DIRECTORIES</c>): a
    ///   RID-specific or self-contained publish (flattened natives), single-file extraction.</item>
    /// <item>beside the ShadowDusk/Vortice assemblies (their <c>runtimes/&lt;rid&gt;/native</c>,
    ///   then flat): a plugin loaded into another host, such as MGCB, whose base directory and
    ///   search directories are the host's.</item>
    /// <item>the base directory, flat.</item>
    /// </list>
    /// </summary>
    internal static IEnumerable<string> GetPinnedPairDirectories(
        string baseDirectory,
        IEnumerable<string> nativeSearchDirectories,
        IEnumerable<string> assemblyDirectories,
        string rid,
        bool ignoreCase)
    {
        var seen = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        IEnumerable<string> All()
        {
            yield return Path.Combine(baseDirectory, "runtimes", rid, "native");
            foreach (string dir in nativeSearchDirectories)
                yield return dir;
            foreach (string dir in assemblyDirectories)
            {
                yield return Path.Combine(dir, "runtimes", rid, "native");
                yield return dir;
            }
            yield return baseDirectory;
        }

        foreach (string dir in All())
        {
            string normalized = Path.TrimEndingDirectorySeparator(dir);
            if (normalized.Length > 0 && seen.Add(normalized))
                yield return normalized;
        }
    }

    /// <summary>The Vortice.Dxc RID for the running process (<c>ProcessArchitecture</c>, never the OS's).</summary>
    internal static string PinnedRid(bool windows, Architecture processArchitecture) =>
        (windows ? "win-" : "linux-") + processArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };

    private static IEnumerable<string> GetAssemblyDirectories()
    {
        foreach (Assembly assembly in new[] { typeof(DxcLoader).Assembly, typeof(Vortice.Dxc.Dxc).Assembly })
        {
            // Empty for single-file bundles (no location); the search directories cover those.
#pragma warning disable IL3000
            string location = assembly.Location;
#pragma warning restore IL3000
            string? directory = location.Length == 0 ? null : Path.GetDirectoryName(location);
            if (!string.IsNullOrEmpty(directory))
                yield return directory;
        }
    }

    /// <summary>Win32 module lookups for <see cref="VerifyBoundWindowsDxil"/>.</summary>
    private static class WindowsModules
    {
        /// <summary>
        /// The full path of the module a bare-name <c>LoadLibrary(name)</c> would return right
        /// now (the already-loaded module of that base name), or <c>null</c> if none is loaded.
        /// </summary>
        internal static string? GetLoadedModulePath(string moduleName)
        {
            IntPtr module = GetModuleHandleW(moduleName);
            if (module == IntPtr.Zero) return null;

            char[] buffer = new char[32768];
            uint length = GetModuleFileNameW(module, buffer, (uint)buffer.Length);
            return length == 0 ? null : new string(buffer, 0, (int)length);
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint GetModuleFileNameW(IntPtr module, [Out] char[] fileName, uint size);
    }

    /// <summary>dyld image enumeration for <see cref="CheckBoundValidator"/>.</summary>
    private static class MacDyld
    {
        private const string LibSystem = "/usr/lib/libSystem.dylib";

        /// <summary>The path of the first loaded image whose leaf name matches, or <c>null</c>.</summary>
        internal static string? FindLoadedImage(Func<string, bool> leafNameMatches)
        {
            try
            {
                uint count = _dyld_image_count();
                for (uint i = 0; i < count; i++)
                {
                    string? path = Marshal.PtrToStringUTF8(_dyld_get_image_name(i));
                    if (path is not null && leafNameMatches(Path.GetFileName(path)))
                        return path;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Cannot enumerate: report nothing rather than fail a compile on a guess.
            }

            return null;
        }

        [DllImport(LibSystem)]
        private static extern uint _dyld_image_count();

        [DllImport(LibSystem)]
        private static extern IntPtr _dyld_get_image_name(uint imageIndex);
    }

    private static IntPtr Resolve(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != DxcLibraryName) return IntPtr.Zero;

        IntPtr handle;

        // Android (Phase 50): the native rides in the APK's per-ABI lib/<abi>/ dir and the
        // Android dynamic linker resolves it by SONAME — a bare-name load, NOT the desktop
        // path-probing below (Android W^X forbids loading executable code from a writable
        // dir, and AppContext.BaseDirectory is the sandboxed app-data dir, not the APK).
        if (OperatingSystem.IsAndroid())
            return NativeLibrary.TryLoad(AndroidLibFileName, out handle) ? handle : IntPtr.Zero;

        // ProcessArchitecture, NOT OSArchitecture: the dylib must match the PROCESS.
        // Under Rosetta 2 (an osx-x64 binary on an arm64 Mac — GitHub's macOS runners
        // do exactly this) OSArchitecture reports Arm64, which made the resolver probe
        // the arm64 dylib an x64 process can never load and miss the x64 one beside it.
        foreach (string candidate in GetProbeCandidates(
                     AppContext.BaseDirectory, RuntimeInformation.ProcessArchitecture))
        {
            if (NativeLibrary.TryLoad(candidate, out handle))
                return handle;
        }

        // NuGet runtimes/<rid>/native asset for framework-dependent consumers (the
        // natives live in the package cache, never the app base directory) — and the
        // single-file extraction dir, where the csproj's per-arch Link paths survive
        // as subdirectories (bug-hunt 2026-07-27 C3: the dylib extracts to
        // <extractionDir>/osx-<arch>/libdxcompiler.dylib, which a flat probe never sees).
        foreach (string dir in GetNativeSearchDirectories())
        {
            foreach (string candidate in GetSearchDirectoryCandidates(
                         dir, RuntimeInformation.ProcessArchitecture))
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                    return handle;
            }
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
        string baseDirectory, Architecture processArchitecture)
    {
        string rid = processArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";

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

    /// <summary>
    /// The candidates probed inside ONE host native-search directory: the per-arch
    /// subdirectory the csproj Link paths produce (which single-file extraction
    /// preserves — bug-hunt 2026-07-27 C3), then flat. Pure (no I/O) so the order
    /// is unit-testable, like <see cref="GetProbeCandidates"/>.
    /// </summary>
    internal static IEnumerable<string> GetSearchDirectoryCandidates(
        string directory, Architecture processArchitecture)
    {
        string rid = processArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        yield return Path.Combine(directory, rid, MacLibFileName);
        yield return Path.Combine(directory, MacLibFileName);
    }

    private static string[] GetNativeSearchDirectories()
    {
        // Set by the host from deps.json (includes each package's runtimes/<rid>/native).
        return AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string dirs
            ? dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            : [];
    }
}
