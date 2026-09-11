#nullable enable

using System.Runtime.InteropServices;

namespace ShadowDusk.Slang;

/// <summary>
/// Resolves the path to the packaged <c>slangc.exe</c> this package bundles for win-x64.
///
/// <para><b>Phase 66 A2 scaffolding only.</b> This proves the PACKAGING plumbing (the
/// native rides inside the ShadowDusk.Slang NuGet under
/// <c>runtimes/win-x64/native/</c> and reaches a consumer's build output) — it is not the
/// compile route. The actual process-based <c>SlangCompiler</c> wrapper (mirroring
/// <c>ShadowDusk.HLSL.Dxc.DxcShaderCompiler</c>'s shape: structured
/// <c>Result&lt;T, ShaderError[]&gt;</c>, verbatim diagnostics) is Phase 66 A3 — see
/// <c>plan/PHASE-66-full-slang-input-implementation.md</c>.</para>
///
/// <para>Adapted from <c>ShadowDusk.HLSL.Vkd3d.Vkd3dLoader</c>'s probe order for a
/// subprocess tool rather than a P/Invoked library: <c>Process.Start</c> needs a file
/// path, not a loaded handle, so this resolves a path and never touches
/// <see cref="System.Runtime.InteropServices.NativeLibrary"/>. Probe order:</para>
/// <list type="number">
///   <item>the app base directory (where the packed <c>runtimes/win-x64/native</c> asset
///   lands for a self-contained or RID-specific publish, and where the csproj's own
///   <c>CopyToOutputDirectory</c> entry places it for repo/dev builds),</item>
///   <item>the host's native search directories (<c>AppContext</c>
///   <c>NATIVE_DLL_SEARCH_DIRECTORIES</c>) — these are the directories the runtime also
///   resolves the NuGet <c>runtimes/&lt;rid&gt;/native</c> asset against for
///   framework-dependent consumers. <b>Unverified for A2:</b> whether
///   <c>slangc.exe</c> actually lands in one of these directories (as opposed to only the
///   NuGet global packages cache) for a plain framework-dependent, non-RID-specific build
///   is not measured here — left for A3 to confirm and, if needed, widen this probe,</item>
///   <item>a <c>tools/slang/win-x64/</c> folder found by walking up from the base
///   directory (repo dev/test runs against <c>tools/restore.ps1</c>'s restored copy;
///   mirrors <c>Vkd3dLoader.FindToolsVkd3d</c>, including its repository-root guard).</item>
/// </list>
/// </summary>
public static class SlangToolPath
{
    private const string SlangcFileName = "slangc.exe";

    /// <summary>
    /// Only win-x64 is packaged today (Phase 66 A2); other RIDs land in a later stage
    /// (plan/PHASE-66-full-slang-input-implementation.md, A2 bullet).
    /// </summary>
    public static bool IsSupportedOnThisPlatform =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>
    /// Resolves the absolute path to the packaged <c>slangc.exe</c>, or <see langword="null"/>
    /// if it cannot be found (unsupported platform, or the native asset is missing —
    /// e.g. a build that never restored <c>tools/slang/win-x64</c>).
    /// </summary>
    public static string? Resolve()
    {
        if (!IsSupportedOnThisPlatform)
            return null;

        string baseCandidate = Path.Combine(AppContext.BaseDirectory, SlangcFileName);
        if (File.Exists(baseCandidate))
            return baseCandidate;

        foreach (string dir in GetNativeSearchDirectories())
        {
            string candidate = Path.Combine(dir, SlangcFileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return FindToolsSlang();
    }

    /// <summary>Like <see cref="Resolve"/>, but throws if <c>slangc.exe</c> cannot be found.</summary>
    /// <exception cref="FileNotFoundException">The native could not be resolved.</exception>
    public static string ResolveOrThrow()
    {
        return Resolve() ?? throw new FileNotFoundException(
            "slangc.exe was not found. ShadowDusk.Slang packages the real Slang compiler " +
            "for win-x64 only today (Phase 66 A2); other RIDs are not yet supported. If " +
            "this is a framework-dependent build that never copies runtimes/win-x64/native " +
            "locally, see the Phase 66 A3 note on SlangToolPath's native-search-directory probe.");
    }

    private static string[] GetNativeSearchDirectories() =>
        AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string dirs
            ? dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            : [];

    /// <summary>
    /// Finds <c>tools/slang/win-x64/slangc.exe</c> in an ancestor of the base directory —
    /// the layout <c>tools/restore.ps1</c>/<c>restore.sh</c> produce in a repo checkout.
    /// Mirrors <c>Vkd3dLoader.FindToolsVkd3d</c>, including the repository-root guard: an
    /// unprivileged <c>C:\tools\slang\</c> elsewhere on the ancestor walk can never be
    /// mistaken for the pinned, hash-verified restored copy.
    /// </summary>
    private static string? FindToolsSlang()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (IsRepositoryRoot(dir))
            {
                string candidate = Path.Combine(dir.FullName, "tools", "slang", "win-x64", SlangcFileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

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
