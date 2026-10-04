#nullable enable

using System.Reflection;
using System.Runtime.InteropServices;
using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Vkd3d;

/// <summary>
/// Makes vkd3d-shader run as ShadowDusk's own pinned build (vkd3d 2.1, restored and packed by
/// <c>tools/restore.*</c>), and only as that build (issue #350, the counterpart of issue #270's
/// DXC fix).
///
/// <para>The binary ships two ways: packed into the ShadowDusk.HLSL NuGet under
/// <c>runtimes/&lt;rid&gt;/native</c>, and copied to build output (flat on Windows and Linux,
/// <c>osx-{x64,arm64}/</c> on macOS, whose arches share one file name) from the restored
/// <c>tools/vkd3d/</c>. <see cref="PinnedNativeLibrary"/> probes those places by absolute path
/// (beside the ShadowDusk assemblies first, so a plugin host such as MGCB finds them), checks
/// each file's SHA-256 against <see cref="Sha256ByRid"/> BEFORE loading it, and on macOS checks
/// the image dyld really mapped. Last comes a <c>tools/vkd3d/</c> folder of a ShadowDusk
/// checkout above the base directory (repo dev/test runs).</para>
///
/// <para><b>Never a bare name.</b> The loader used to finish with
/// <c>NativeLibrary.TryLoad("libvkd3d-shader-1.dll")</c> (Linux <c>libvkd3d-shader.so.1</c>,
/// macOS <c>libvkd3d-shader.1.dylib</c>), which is the OS search: <c>PATH</c>,
/// <c>LD_LIBRARY_PATH</c>, <c>DYLD_LIBRARY_PATH</c>. Wine ships a <c>libvkd3d-shader</c>, so a
/// different vkd3d compiled DirectX 11 and FNA whenever the app-local probe missed, which it
/// always did inside MGCB. Now a missing or foreign library is <see cref="LoadErrorCode"/>, and
/// the import resolver throws rather than return <see cref="IntPtr.Zero"/> (which would hand
/// the request to the runtime's default probing, and through it to the same OS search).</para>
///
/// <para><b>Android and other operating systems:</b> ShadowDusk ships no vkd3d for them (DirectX
/// and FNA compile on the desktop), so the result is <see cref="LoadErrorCode"/>; nothing is ever
/// loaded by name from the APK or the system.</para>
/// </summary>
internal static class Vkd3dLoader
{
    /// <summary>The diagnostic for a missing, unloadable or foreign vkd3d-shader.</summary>
    internal const string LoadErrorCode = "SD0211";

    /// <summary>
    /// The SHA-256 of every vkd3d-shader file ShadowDusk ships, by RID. These are the pins in
    /// <c>tools/restore.ps1</c> and <c>tools/restore.sh</c> (<c>Restore-Vkd3dShader</c>);
    /// <c>Vkd3dLoaderTests</c> fails if they drift apart or from the files the build copies.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Sha256ByRid = new Dictionary<string, string>
    {
        ["win-x64"] = "9b97222601ee00ffc60f9f7bc426e13cc7df325c46725fe80a036e3eff9e2edb",
        ["linux-x64"] = "bda15bc2a8b1a017a4adfed3c00c85f696cebb41f629b715ad8f58a37bc26678",
        ["osx-x64"] = "0fcd4e99d7c4c9b0375535ccfd0c27cf2f72fe60fb28ca7ffc770616d58c09f6",
        ["osx-arm64"] = "b624db5641469c34bc44849cbc42201ffd7cdd26bfc8bb6a4c8f5f6757f8d34b",
    };

    private static readonly object RegisterGate = new();
    private static volatile bool _registered;
    // Cached once it succeeds; a failed load is retried on the next compile (a transient
    // failure, e.g. a file briefly locked, must not refuse every compile for the process).
    private static readonly PinnedNativeLibrary.RetryableLoad Loaded = new(Load);

    /// <summary>
    /// Idempotently installs the import resolver and loads the pinned library, returning
    /// <c>null</c> when vkd3d-shader may be used, or the <see cref="LoadErrorCode"/> error the
    /// caller must return instead of compiling. A success is final for the process; a failure is
    /// retried on the next call.
    /// A lock (not a lone CAS) so a concurrent second caller BLOCKS until the winner has
    /// finished installing the resolver (the DxcLoader race class, observed as an intermittent
    /// DllNotFoundException under test parallelism).
    /// </summary>
    public static ShaderError? Register()
    {
        if (!_registered)
        {
            lock (RegisterGate)
            {
                if (!_registered)
                {
                    RegisterCore();
                    _registered = true;
                }
            }
        }

        return Loaded.Value.Error;
    }

    /// <summary>The file the pinned library was loaded from, or <c>null</c> (not loaded, or refused).</summary>
    internal static string? LoadedPath => Loaded.Succeeded?.Path;

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

        NativeLibrary.SetDllImportResolver(typeof(Vkd3dLoader).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Vkd3dNative.LibName) return IntPtr.Zero;

        // Never IntPtr.Zero for our library: that would let the runtime's default probing
        // load whatever the OS search finds. Callers check Register() first, so this only
        // fires for a P/Invoke that skipped it.
        PinnedNativeLibrary.LoadResult result = Loaded.Value;
        return result.Error is null ? result.Handle : throw new DllNotFoundException(result.Error.Message);
    }

    private static PinnedNativeLibrary.LoadResult Load()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return new PinnedNativeLibrary.LoadResult(IntPtr.Zero, null, new ShaderError(
                File: "", Line: 0, Column: 0, Code: LoadErrorCode,
                Message: $"ShadowDusk bundles no vkd3d-shader for this operating system ({RuntimeInformation.OSDescription}, " +
                         $"{RuntimeInformation.RuntimeIdentifier}), so no DirectX 11 or FNA compile can run here. It ships " +
                         "its pinned vkd3d-shader for Windows, Linux and macOS only, and will not use one found elsewhere. " +
                         "Compile DirectX 11 and FNA effects on the desktop."));
        }

        var native = new PinnedNative(
            DisplayName: "vkd3d-shader 2.1",
            ErrorCode: LoadErrorCode,
            Origin: $"the ShadowDusk.HLSL package's runtimes/{PinnedNativeLibrary.CurrentRid()}/native",
            Consequence: "no DirectX 11 or FNA compile can run",
            ProbeExport: "vkd3d_shader_compile",
            FileNames: GetLibFileNames(),
            Sha256ByRid: Sha256ByRid,
            MismatchHint: "The usual cause is another package or a manual copy placing its own vkd3d-shader " +
                          "where ShadowDusk.HLSL deploys it; remove it so the file the ShadowDusk.HLSL package " +
                          "ships is the one deployed (in a ShadowDusk checkout, re-run tools/restore).");

        return PinnedNativeLibrary.Load(native, [typeof(Vkd3dLoader).Assembly], RepositoryCandidates());
    }

    private static void SetDefaultEnvironmentVariable(string name, string value)
    {
        if (Environment.GetEnvironmentVariable(name) is null)
            Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    /// The restored copies under a ShadowDusk checkout's <c>tools/vkd3d/</c> (repo dev/test runs
    /// out of bin/), probed LAST and still identity-checked.
    /// </summary>
    private static IEnumerable<string> RepositoryCandidates()
    {
        foreach (string subdir in GetProbeSubdirectories())
        {
            foreach (string fileName in GetLibFileNames())
            {
                if (FindToolsVkd3d(Path.Combine(subdir, fileName)) is { } candidate)
                    yield return candidate;
            }
        }
    }

    /// <summary>
    /// Finds <c>tools/vkd3d/&lt;relativePath&gt;</c> in an ancestor of the base directory —
    /// the layout <c>tools/restore.{ps1,sh}</c> produces in a repo checkout, so dev and
    /// test runs find the restored native without a copy step.
    ///
    /// <para>The ancestor must ALSO look like a ShadowDusk checkout (it carries the
    /// solution file or a <c>.git</c> entry). Without that marker the walk ran all the way
    /// to the volume root, where <c>C:\tools\vkd3d\</c> is a path any unprivileged local
    /// account can create — an uncontrolled search path (CWE-427). The SHA-256 check now
    /// refuses a planted file there regardless; the bound keeps an unrelated
    /// <c>tools/vkd3d</c> from even being opened.</para>
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

    /// <summary>
    /// The file names vkd3d-shader ships under on this OS, in probe order: Windows
    /// <c>libvkd3d-shader-1.dll</c>; Linux <c>libvkd3d-shader.so.1</c> (then <c>.so</c>);
    /// macOS <c>libvkd3d-shader.1.dylib</c> (then <c>.dylib</c>).
    /// </summary>
    internal static string[] GetLibFileNames()
    {
        if (OperatingSystem.IsWindows())
            return ["libvkd3d-shader-1.dll"];
        if (OperatingSystem.IsMacOS())
            return ["libvkd3d-shader.1.dylib", "libvkd3d-shader.dylib"];
        return ["libvkd3d-shader.so.1", "libvkd3d-shader.so"];
    }

    /// <summary>
    /// Relative directories to probe under tools/vkd3d/. macOS restores per-arch
    /// (osx-x64 / osx-arm64 share a dylib file name); everywhere else the layout is flat.
    /// </summary>
    private static string[] GetProbeSubdirectories()
    {
        if (!OperatingSystem.IsMacOS())
            return [""];
        // ProcessArchitecture, not OSArchitecture: under Rosetta 2 the OS is Arm64
        // but only an x64 dylib can load into the x64 process (see DxcLoader.Resolve).
        return [PinnedNativeLibrary.CurrentRid(), ""];
    }
}
