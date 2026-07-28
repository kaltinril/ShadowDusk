#nullable enable

using System.Runtime.InteropServices;

namespace ShadowDusk.HLSL.Vkd3d;

/// <summary>
/// Resolves the native vkd3d-shader library per OS, mirroring
/// <c>ShadowDusk.GLSL.Interop.SpvcLoader</c>.
///
/// The binary ships two ways: packed into the ShadowDusk.HLSL NuGet under
/// <c>runtimes/&lt;rid&gt;/native</c> (when the per-RID artifact was restored at pack
/// time), and as a restored artifact under <c>tools/vkd3d/</c> for repo builds
/// (see tools/restore.ps1). The resolver probes, in order:
///   1. the app base directory (where the .csproj copies it next to the binaries),
///   2. a <c>tools/vkd3d/</c> folder found by walking up from the base directory
///      (covers running straight out of bin/ during dev/tests),
///   3. the host's native search directories (<c>NATIVE_DLL_SEARCH_DIRECTORIES</c>)
///      — this is how the NuGet <c>runtimes/&lt;rid&gt;/native</c> asset resolves for
///      framework-dependent consumers, whose natives live in the package cache and
///      never appear in the app base directory (and whose file names, e.g.
///      <c>libvkd3d-shader-1.dll</c>, do not match default bare-name probing),
///   4. a bare load by file name (single-file publish extracts natives to a temp
///      dir already on the native search path; also lets the OS loader use PATH).
///
/// Per-OS file names: Windows <c>libvkd3d-shader-1.dll</c>; Linux
/// <c>libvkd3d-shader.so.1</c> (then <c>.so</c>); macOS
/// <c>libvkd3d-shader.1.dylib</c> (then <c>.dylib</c>).
///
/// On macOS the restored layout is per-arch (<c>osx-x64/</c> / <c>osx-arm64/</c>
/// subdirectories — both arches share one dylib file name, so they cannot sit flat
/// side by side); probes 1 and 2 check the current process arch's subdirectory
/// first, then the flat path (a manually-placed dylib).
/// </summary>
internal static class Vkd3dLoader
{
    private static readonly object RegisterGate = new();
    private static volatile bool _registered;

    // A lock (not a lone CAS) so a concurrent second caller BLOCKS until the winner
    // has finished installing the resolver — with CAS-then-subscribe the loser could
    // return and P/Invoke before the resolver existed (the DxcLoader race class,
    // observed as an intermittent DllNotFoundException under test parallelism).
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
        // Silence vkd3d's internal debug logging by default (e.g.
        // "vkd3d:1234:fixme:preproc_yyparse #line directive." on stderr). A successful
        // compile must be SILENT — the mgfxc contract: MGCB treats stderr output as
        // diagnostics, and a consumer's process should not get native debug noise on
        // its stderr. Real compile errors are unaffected: they flow through vkd3d's
        // messages out-parameter (constraint 5), not this debug channel. An explicit
        // user setting is respected (debugging escape hatch, never required for
        // correct output). Must run BEFORE the native library loads — vkd3d caches
        // its debug level from the environment on first use.
        SetDefaultEnvironmentVariable("VKD3D_DEBUG", "none");
        SetDefaultEnvironmentVariable("VKD3D_SHADER_DEBUG", "none");

        NativeLibrary.SetDllImportResolver(
            typeof(Vkd3dLoader).Assembly,
            (name, _, _) =>
            {
                if (name != Vkd3dNative.LibName) return IntPtr.Zero;

                foreach (string fileName in GetLibFileNames())
                {
                    IntPtr handle;
                    foreach (string subdir in GetProbeSubdirectories())
                    {
                        // 1. Next to the app binaries (csproj copy step; per-arch
                        // subdir first on macOS, then flat).
                        string baseCandidate = Path.Combine(AppContext.BaseDirectory, subdir, fileName);
                        if (NativeLibrary.TryLoad(baseCandidate, out handle))
                            return handle;
                    }

                    // 2. The host's native search directories — resolves the NuGet
                    // runtimes/<rid>/native asset for framework-dependent consumers,
                    // and the single-file extraction dir, where the csproj's per-arch
                    // macOS Link paths survive as subdirectories (bug-hunt 2026-07-27
                    // C3: the dylib extracts to <extractionDir>/osx-<arch>/, which a
                    // flat probe never sees).
                    foreach (string dir in GetNativeSearchDirectories())
                    {
                        foreach (string subdir in GetProbeSubdirectories())
                        {
                            string candidate = Path.Combine(dir, subdir, fileName);
                            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                                return handle;
                        }
                    }

                    // 3. A tools/vkd3d folder above the base directory (repo dev/test runs).
                    //
                    // DELIBERATELY LAST of the directory probes. This walk ascends toward
                    // the filesystem root, and on Windows the volume root is
                    // add-subdirectory-writable by ordinary users, so `C:\tools\vkd3d\` is
                    // a directory any local account can create. Running it AHEAD of the
                    // packaged-asset probe made that an uncontrolled search path
                    // (CWE-427): a framework-dependent consumer — whose native lives in
                    // the NuGet cache and is only reachable via step 2 — would have loaded
                    // and executed the planted DLL instead. It is also bounded to a real
                    // ShadowDusk checkout now, so an unrelated `tools/vkd3d` on the path
                    // can no longer silently override the pinned, hash-verified native.
                    foreach (string subdir in GetProbeSubdirectories())
                    {
                        string? toolsCandidate = FindToolsVkd3d(Path.Combine(subdir, fileName));
                        if (toolsCandidate is not null && NativeLibrary.TryLoad(toolsCandidate, out handle))
                            return handle;
                    }

                    // 4. Bare name (single-file publish temp dir / OS search path).
                    if (NativeLibrary.TryLoad(fileName, out handle))
                        return handle;
                }

                return IntPtr.Zero;
            });
    }

    private static void SetDefaultEnvironmentVariable(string name, string value)
    {
        if (Environment.GetEnvironmentVariable(name) is null)
            Environment.SetEnvironmentVariable(name, value);
    }

    private static string[] GetNativeSearchDirectories()
    {
        // Set by the host from deps.json (includes each package's runtimes/<rid>/native).
        return AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string dirs
            ? dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            : [];
    }

    /// <summary>
    /// Finds <c>tools/vkd3d/&lt;relativePath&gt;</c> in an ancestor of the base directory —
    /// the layout <c>tools/restore.{ps1,sh}</c> produces in a repo checkout, so dev and
    /// test runs find the restored native without a copy step.
    ///
    /// <para>The ancestor must ALSO look like a ShadowDusk checkout (it carries the
    /// solution file or a <c>.git</c> entry). Without that marker the walk ran all the way
    /// to the volume root, where <c>C:\tools\vkd3d\</c> is a path any unprivileged local
    /// account can create — an uncontrolled search path that would have let a planted DLL
    /// execute inside another user's process, and that could silently displace the pinned,
    /// hash-verified native whose byte-stability is a product promise.</para>
    /// </summary>
    private static string? FindToolsVkd3d(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (IsRepositoryRoot(dir))
            {
                string candidate = Path.Combine(dir.FullName, "tools", "vkd3d", relativePath);
                if (File.Exists(candidate))
                    return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// A marker that an unprivileged user cannot cheaply forge in a directory they do not
    /// already own. A bare <c>.git</c> ENTRY is not enough: <c>C:\.git</c> is a directory
    /// any local account can create, which would re-open the very search path this bound
    /// exists to close. So the solution file is the primary marker, and a <c>.git</c> is
    /// accepted only when it is structurally a real repository (a directory containing
    /// <c>objects</c>, or the <c>gitdir:</c> pointer file a worktree/submodule uses).
    /// </summary>
    private static bool IsRepositoryRoot(DirectoryInfo dir)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
            return true;

        string dotGit = Path.Combine(dir.FullName, ".git");
        if (Directory.Exists(dotGit))
            return Directory.Exists(Path.Combine(dotGit, "objects"));

        if (File.Exists(dotGit))
        {
            try
            {
                // git worktree / submodule: a one-line file `gitdir: <path>`.
                return File.ReadAllText(dotGit).StartsWith("gitdir:", StringComparison.Ordinal);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    private static string[] GetLibFileNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ["libvkd3d-shader-1.dll"];
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ["libvkd3d-shader.1.dylib", "libvkd3d-shader.dylib"];
        return ["libvkd3d-shader.so.1", "libvkd3d-shader.so"];
    }

    /// <summary>
    /// Relative directories to probe under the base directory and tools/vkd3d/.
    /// macOS restores per-arch (osx-x64 / osx-arm64 share a dylib file name);
    /// everywhere else the layout is flat.
    /// </summary>
    private static string[] GetProbeSubdirectories()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return [""];
        // ProcessArchitecture, not OSArchitecture: under Rosetta 2 the OS is Arm64
        // but only an x64 dylib can load into the x64 process (see DxcLoader.Resolve).
        string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "osx-arm64" : "osx-x64";
        return [arch, ""];
    }
}
