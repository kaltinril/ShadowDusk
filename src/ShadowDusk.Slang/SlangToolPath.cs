#nullable enable

using System.Runtime.InteropServices;

namespace ShadowDusk.Slang;

/// <summary>
/// Resolves the packaged <c>slangc</c> executable this package bundles for <c>win-x64</c>,
/// <c>linux-x64</c>, <c>osx-x64</c> and <c>osx-arm64</c> (issue #227).
///
/// <para>Adapted from <c>ShadowDusk.HLSL.Vkd3d.Vkd3dLoader</c>'s probe order for a
/// subprocess tool rather than a P/Invoked library: <c>Process.Start</c> needs a file
/// path, not a loaded handle, so this resolves a path and never touches
/// <see cref="NativeLibrary"/>. Probe order, first hit wins:</para>
/// <list type="number">
///   <item>the app base directory: where a self-contained or RID-specific publish flattens
///   the package's <c>runtimes/&lt;rid&gt;/native</c> assets, and where the csproj's own
///   <c>CopyToOutputDirectory</c> places the win-x64 copy for repo/dev builds;</item>
///   <item><c>&lt;base&gt;/runtimes/&lt;rid&gt;/native/</c>: where a plain framework-dependent,
///   non-RID-specific build of a NuGet consumer keeps every RID's native assets (measured by
///   <c>.github/workflows/pack-consume.yml</c>'s Slang consumer, issue #225);</item>
///   <item>the host's native search directories (<c>AppContext</c>
///   <c>NATIVE_DLL_SEARCH_DIRECTORIES</c>), the directories the runtime itself resolved
///   native assets from;</item>
///   <item>a <c>tools/slang/&lt;rid&gt;/</c> folder found by walking up from the base
///   directory to a repository root (repo dev/test runs against <c>tools/restore.*</c>'s
///   restored copy; mirrors <c>Vkd3dLoader.FindToolsVkd3d</c>, including its
///   repository-root guard).</item>
/// </list>
/// </summary>
public static class SlangToolPath
{
    /// <summary>
    /// The pinned upstream slangc release. Unix library file names carry it
    /// (<c>libslang-compiler.so.0.&lt;version&gt;</c>), because that is the exact name
    /// slangc's own <c>NEEDED</c>/<c>LC_LOAD_DYLIB</c> entry asks the loader for. Keep in
    /// sync with <c>tools/restore.sh</c>/<c>restore.ps1</c>
    /// (<c>SlangToolPathTests.SlangVersion_MatchesTheRestoreScriptPins</c> enforces it).
    /// </summary>
    internal const string SlangVersion = "2026.14.1";

    /// <summary>
    /// Every upstream macOS build of the pinned release (x86_64 and aarch64, the standard
    /// and the <c>-dist</c> zips alike) declares <c>LC_BUILD_VERSION minos 26.0</c>, so dyld
    /// refuses to load it on anything older. Measured with <c>otool -l</c>, issue #227.
    /// </summary>
    internal static readonly Version MinimumMacOSVersion = new(26, 0);

    /// <summary>The RIDs this package bundles slangc for.</summary>
    internal static readonly IReadOnlyList<string> SupportedRids = ["win-x64", "linux-x64", "osx-x64", "osx-arm64"];

    /// <summary>
    /// <see langword="true"/> when this host can run the bundled slangc: a supported RID
    /// (see <see cref="SupportedRids"/>) and, on macOS, macOS 26 or later.
    /// </summary>
    public static bool IsSupportedOnThisPlatform => GetUnsupportedReason() is null;

    /// <summary>The bundled RID matching this process, or <see langword="null"/>.</summary>
    internal static string? CurrentRid =>
        RidFor(OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(),
               RuntimeInformation.ProcessArchitecture);

    /// <summary>
    /// Maps an OS + process architecture to the bundled RID. The process architecture, not
    /// the machine's, decides: an x64 process on Apple Silicon (Rosetta 2) runs the
    /// <c>osx-x64</c> slangc. Android reports <c>IsLinux() == false</c>, so it maps to
    /// <see langword="null"/> (no Android slangc is bundled).
    /// </summary>
    internal static string? RidFor(bool isWindows, bool isLinux, bool isMacOS, Architecture architecture) =>
        (isWindows, isLinux, isMacOS, architecture) switch
        {
            (true, _, _, Architecture.X64) => "win-x64",
            (_, true, _, Architecture.X64) => "linux-x64",
            (_, _, true, Architecture.X64) => "osx-x64",
            (_, _, true, Architecture.Arm64) => "osx-arm64",
            _ => null,
        };

    /// <summary>
    /// <see langword="null"/> when this host can run the bundled slangc; otherwise a
    /// sentence naming why, used verbatim in <c>SD0620</c>.
    /// </summary>
    internal static string? GetUnsupportedReason() =>
        GetUnsupportedReason(CurrentRid, OperatingSystem.IsMacOS(), Environment.OSVersion.Version,
                             RuntimeInformation.RuntimeIdentifier);

    internal static string? GetUnsupportedReason(string? rid, bool isMacOS, Version osVersion, string hostRid)
    {
        if (rid is null)
        {
            return $"ShadowDusk.Slang bundles slangc for {string.Join(", ", SupportedRids)}; " +
                   $"this host ({hostRid}) is not one of them.";
        }

        if (isMacOS && osVersion < MinimumMacOSVersion)
        {
            return $"ShadowDusk.Slang's bundled slangc (upstream v{SlangVersion}) is built for macOS " +
                   $"{MinimumMacOSVersion.Major} or later, and this host runs macOS {osVersion}.";
        }

        return null;
    }

    /// <summary>The slangc executable's file name for <paramref name="rid"/>.</summary>
    internal static string ExecutableFileName(string rid) =>
        rid.StartsWith("win-", StringComparison.Ordinal) ? "slangc.exe" : "slangc";

    /// <summary>
    /// The slang-compiler library's file name for <paramref name="rid"/>: the one other file
    /// slangc needs at run time (the measured minimal set, Phase 66 A2 / issue #227).
    /// </summary>
    internal static string CompilerLibraryFileName(string rid) => rid switch
    {
        "win-x64" => "slang-compiler.dll",
        "linux-x64" => $"libslang-compiler.so.0.{SlangVersion}",
        "osx-x64" or "osx-arm64" => $"libslang-compiler.0.{SlangVersion}.dylib",
        _ => throw new ArgumentOutOfRangeException(nameof(rid), rid, "not a bundled slangc RID"),
    };

    /// <summary>
    /// Resolves the absolute path to the packaged slangc, or <see langword="null"/> if this
    /// host is unsupported (see <see cref="IsSupportedOnThisPlatform"/>) or the native asset
    /// is missing (e.g. a build that never restored <c>tools/slang/&lt;rid&gt;</c>).
    /// </summary>
    public static string? Resolve()
    {
        string? rid = CurrentRid;
        if (rid is null || GetUnsupportedReason() is not null)
            return null;

        return Resolve(rid, AppContext.BaseDirectory, GetNativeSearchDirectories());
    }

    /// <summary>The probe itself, with every host input explicit (the test seam).</summary>
    internal static string? Resolve(string rid, string baseDirectory, IReadOnlyList<string> nativeSearchDirectories)
    {
        string exe = ExecutableFileName(rid);

        string baseCandidate = Path.Combine(baseDirectory, exe);
        if (File.Exists(baseCandidate))
            return baseCandidate;

        string runtimesCandidate = Path.Combine(baseDirectory, "runtimes", rid, "native", exe);
        if (File.Exists(runtimesCandidate))
            return runtimesCandidate;

        foreach (string dir in nativeSearchDirectories)
        {
            string candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
                return candidate;
        }

        return FindToolsSlang(rid, baseDirectory);
    }

    /// <summary>Like <see cref="Resolve()"/>, but throws if slangc cannot be found.</summary>
    /// <exception cref="PlatformNotSupportedException">This host cannot run the bundled slangc.</exception>
    /// <exception cref="FileNotFoundException">The native could not be resolved.</exception>
    public static string ResolveOrThrow() =>
        ResolveOrThrow(GetUnsupportedReason(), CurrentRid, AppContext.BaseDirectory, GetNativeSearchDirectories());

    /// <summary><see cref="ResolveOrThrow()"/> with every host input explicit (the test seam).</summary>
    internal static string ResolveOrThrow(
        string? unsupportedReason, string? rid, string baseDirectory, IReadOnlyList<string> nativeSearchDirectories)
    {
        if (unsupportedReason is not null)
            throw new PlatformNotSupportedException(unsupportedReason);
        if (rid is null)
            throw new PlatformNotSupportedException("No bundled slangc RID for this host.");

        return Resolve(rid, baseDirectory, nativeSearchDirectories) ?? throw new FileNotFoundException(
            $"slangc was not found for {rid}. Probed the app base directory, " +
            $"runtimes/{rid}/native/ under it, the host's native search directories, and " +
            $"a repository tools/slang/{rid}/ restore (tools/restore.sh / restore.ps1).",
            ExecutableFileName(rid));
    }

    private static string[] GetNativeSearchDirectories() =>
        AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string dirs
            ? dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            : [];

    /// <summary>
    /// Finds <c>tools/slang/&lt;rid&gt;/&lt;slangc&gt;</c> in an ancestor of the base
    /// directory, the layout <c>tools/restore.*</c> produce in a repo checkout. Mirrors
    /// <c>Vkd3dLoader.FindToolsVkd3d</c>, including the repository-root guard: an
    /// unprivileged <c>C:\tools\slang\</c> elsewhere on the ancestor walk can never be
    /// mistaken for the pinned, hash-verified restored copy.
    /// </summary>
    private static string? FindToolsSlang(string rid, string baseDirectory)
    {
        string exe = ExecutableFileName(rid);
        var dir = new DirectoryInfo(baseDirectory);
        while (dir is not null)
        {
            if (IsRepositoryRoot(dir))
            {
                string candidate = Path.Combine(dir.FullName, "tools", "slang", rid, exe);
                if (File.Exists(candidate))
                    return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    internal static bool IsRepositoryRoot(DirectoryInfo dir)
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
}
