#nullable enable

namespace ShadowDusk.Slang;

/// <summary>
/// Makes sure <c>slangc.exe</c> can actually be RUN from somewhere writable.
///
/// <para><b>Phase 66 A2's open finding, closed here:</b> <c>slangc.exe</c> writes a runtime
/// cache file (<c>slang-glsl-module.bin</c>, ~1.3 MB) into its OWN directory on first
/// compile — confirmed empirically for this stage (A3) by running it from a separate,
/// writable working directory with the native copied elsewhere: the cache file still lands
/// next to the exe, never in the process's working directory. <c>slangc -h</c> exposes no
/// flag or environment variable to redirect or disable it (checked against the full help
/// text of the pinned v2026.14.1 build; nothing in the General/Target/Downstream/
/// Debugging/Experimental/Deprecated categories mentions a cache path). So the packaged
/// native's directory MUST be writable at runtime, not just readable — and it will not be
/// for every deployment: a self-contained/RID-specific publish copies the native into the
/// app's own output directory (writable), but a framework-dependent build can resolve it
/// straight out of the NuGet global-packages cache, which package managers mark read-only
/// by convention (and a shared multi-user cache may not even be owned by the consumer's
/// account).</para>
///
/// <para><b>The fix:</b> probe the packaged directory for write access first (the common
/// case — a repo/dev build or a self-contained publish — needs no copy at all, so the
/// happy path pays zero extra I/O). Only when that probe fails does this copy
/// <c>slangc.exe</c> + <c>slang-compiler.dll</c> (the true minimal vendored set, Phase 66
/// A2) into a writable per-user cache directory and run from there instead. This was
/// chosen over the other two options A2's finding raised — a "run with a writable working
/// directory" flag does nothing (the write target is the EXE's directory, not the cwd, so
/// no working-directory choice can dodge it) and a "just always copy" default would
/// silently pay a ~24 MiB copy on every process on hosts where it was never needed. The
/// copy is itself idempotent and safe under concurrent first-use (copy-to-temp-then-move,
/// keyed by the packaged files' byte lengths so a native upgrade invalidates the old
/// cache instead of silently reusing a stale copy).</para>
/// </summary>
internal static class SlangNativeCache
{
    private const string SlangcFileName = "slangc.exe";
    private const string SlangCompilerDllFileName = "slang-compiler.dll";

    /// <summary>
    /// Returns a directory containing <c>slangc.exe</c> + <c>slang-compiler.dll</c> that is
    /// safe to invoke slangc from (writable, so its first-compile cache write succeeds):
    /// the packaged directory itself when it is already writable, otherwise a copy under a
    /// writable per-user cache directory.
    /// </summary>
    /// <param name="packagedSlangcPath">
    /// The packaged <c>slangc.exe</c> path, as resolved by <see cref="SlangToolPath.Resolve"/>.
    /// <c>slang-compiler.dll</c> is expected alongside it (A2's packaging shape).
    /// </param>
    /// <exception cref="FileNotFoundException">
    /// The packaged directory is not writable AND <c>slang-compiler.dll</c> is missing next
    /// to <paramref name="packagedSlangcPath"/> (a broken/partial native restore).
    /// </exception>
    public static string EnsureWritableToolDirectory(string packagedSlangcPath) =>
        EnsureWritableToolDirectory(packagedSlangcPath, IsWritable);

    /// <summary>
    /// Test seam: <paramref name="isWritable"/> replaces the real write-access probe, since
    /// Windows' directory <c>ReadOnly</c> attribute does not actually block file creation
    /// inside the directory (verified empirically, Phase 66 A3) — there is no cheap, portable
    /// way to make a temp directory genuinely unwritable from a test without OS-level ACLs.
    /// </summary>
    internal static string EnsureWritableToolDirectory(string packagedSlangcPath, Func<string, bool> isWritable)
    {
        string packagedDir = Path.GetDirectoryName(packagedSlangcPath)
            ?? throw new ArgumentException(
                $"'{packagedSlangcPath}' has no parent directory.", nameof(packagedSlangcPath));

        if (isWritable(packagedDir))
            return packagedDir;

        string dllPath = Path.Combine(packagedDir, SlangCompilerDllFileName);
        var exeInfo = new FileInfo(packagedSlangcPath);
        var dllInfo = new FileInfo(dllPath);
        if (!exeInfo.Exists)
            throw new FileNotFoundException("slangc.exe not found.", packagedSlangcPath);
        if (!dllInfo.Exists)
            throw new FileNotFoundException(
                "slang-compiler.dll not found next to slangc.exe (a partial native restore?).", dllPath);

        // Keyed by byte length only — cheap (no hashing a 24 MiB DLL on every resolve) and
        // sufficient: it collapses to the packaged directory itself whenever that is
        // writable (the common case), and a native version bump changes at least one of
        // the two file sizes in practice, which naturally invalidates a stale cache entry
        // instead of silently reusing it.
        string cacheKey = $"{exeInfo.Length:x}-{dllInfo.Length:x}";
        string cacheDir = Path.Combine(GetCacheRoot(), cacheKey);
        string cachedExe = Path.Combine(cacheDir, SlangcFileName);
        string cachedDll = Path.Combine(cacheDir, SlangCompilerDllFileName);

        if (IsUpToDate(cachedExe, exeInfo.Length) && IsUpToDate(cachedDll, dllInfo.Length))
            return cacheDir;

        Directory.CreateDirectory(cacheDir);
        CopyAtomically(packagedSlangcPath, cachedExe);
        CopyAtomically(dllPath, cachedDll);
        return cacheDir;
    }

    private static bool IsUpToDate(string cachedPath, long expectedLength) =>
        File.Exists(cachedPath) && new FileInfo(cachedPath).Length == expectedLength;

    private static string GetCacheRoot()
    {
        string root;
        try
        {
            root = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create);
        }
        catch (PlatformNotSupportedException)
        {
            root = "";
        }
        if (string.IsNullOrEmpty(root))
            root = Path.GetTempPath();

        return Path.Combine(root, "ShadowDusk", "Slang");
    }

    /// <summary>Writes and deletes a throwaway marker file to test write access — cheap and reliable.</summary>
    private static bool IsWritable(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, $".sd-write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies to a temp name in the destination directory, then renames into place.
    /// <see cref="File.Move(string, string, bool)"/> is a same-volume rename (atomic), so a
    /// concurrent first use from another process racing the same copy converges on
    /// identical bytes either way rather than a torn file.
    /// </summary>
    private static void CopyAtomically(string source, string destination)
    {
        string tempDest = $"{destination}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.Copy(source, tempDest, overwrite: true);
            File.Move(tempDest, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempDest))
                File.Delete(tempDest);
        }
    }
}
