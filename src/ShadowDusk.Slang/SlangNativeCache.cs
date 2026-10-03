#nullable enable

namespace ShadowDusk.Slang;

/// <summary>
/// Makes sure the packaged slangc can actually be RUN: from a writable directory, with its
/// execute bit set.
///
/// <para><b>Writable directory (Phase 66 A3):</b> slangc writes a runtime cache file
/// (<c>slang-glsl-module.bin</c>, ~1.3 MB) into its OWN directory on first compile, never
/// the process's working directory (measured on win-x64 in A3 and on osx-arm64/osx-x64 for
/// issue #227), and <c>slangc -h</c> exposes no flag or environment variable to redirect
/// it. A self-contained/RID-specific publish copies the native into the app's own
/// (writable) output directory, but a framework-dependent build can resolve it from a
/// location the consumer cannot write to (a shared or read-only NuGet cache).</para>
///
/// <para><b>Execute bit (issue #227):</b> the package cannot control the mode its Unix slangc
/// gets. NuGet extracts every package file as <c>rwxr--r--</c> (measured on macOS, issue
/// #225), which is executable only for the user that extracted it: a cache populated by
/// another account, or any copy that lost the bit, would fail <c>Process.Start</c> with
/// "permission denied". This sets <c>u+x,g+x,o+x</c> in place when it can.</para>
///
/// <para><b>The fix for both:</b> use the packaged directory when it is writable and the
/// executable is (or can be made) executable; the common case pays no copy. Otherwise copy
/// slangc + its compiler library into a per-user cache directory, set the execute bit
/// there, and run from that copy. The copy is idempotent and safe under concurrent first
/// use (copy-to-temp-then-move, keyed by the packaged files' byte lengths so a native
/// upgrade invalidates the old cache instead of silently reusing a stale copy).</para>
/// </summary>
internal static class SlangNativeCache
{
    /// <summary>
    /// Returns the path of a slangc executable that is safe to invoke: the packaged one
    /// itself when its directory is writable and it is executable, otherwise a prepared
    /// copy under a writable per-user cache directory. Callers run it with the returned
    /// file's directory as the working directory.
    /// </summary>
    /// <param name="packagedSlangcPath">The packaged slangc path, as resolved by
    /// <see cref="SlangToolPath.Resolve()"/>. Its compiler library must sit alongside it.</param>
    /// <exception cref="FileNotFoundException">The executable or its compiler library is
    /// missing (a partial native restore).</exception>
    public static string EnsureRunnableSlangc(string packagedSlangcPath) =>
        EnsureRunnableSlangc(packagedSlangcPath, RidOf(packagedSlangcPath), IsWritable, TryEnsureExecutable, GetCacheRoot());

    /// <summary>
    /// Test seam: <paramref name="isWritable"/> and <paramref name="tryEnsureExecutable"/>
    /// replace the real probes, since there is no cheap, portable way to make a temp
    /// directory genuinely unwritable from a test (Windows' directory <c>ReadOnly</c>
    /// attribute does not block file creation; verified in Phase 66 A3). <paramref name="cacheRoot"/>
    /// keeps test copies out of the real per-user cache.
    /// </summary>
    internal static string EnsureRunnableSlangc(
        string packagedSlangcPath,
        string rid,
        Func<string, bool> isWritable,
        Func<string, bool> tryEnsureExecutable,
        string cacheRoot)
    {
        string packagedDir = Path.GetDirectoryName(packagedSlangcPath)
            ?? throw new ArgumentException(
                $"'{packagedSlangcPath}' has no parent directory.", nameof(packagedSlangcPath));
        string exeName = Path.GetFileName(packagedSlangcPath);
        string libName = SlangToolPath.CompilerLibraryFileName(rid);
        string libPath = Path.Combine(packagedDir, libName);

        var exeInfo = new FileInfo(packagedSlangcPath);
        var libInfo = new FileInfo(libPath);
        if (!exeInfo.Exists)
            throw new FileNotFoundException($"{exeName} not found.", packagedSlangcPath);
        if (!libInfo.Exists)
            throw new FileNotFoundException(
                $"{libName} not found next to {exeName} (a partial native restore?).", libPath);

        if (isWritable(packagedDir) && tryEnsureExecutable(packagedSlangcPath))
            return packagedSlangcPath;

        // Keyed by byte length only: cheap (no hashing a ~30 MB library on every resolve) and
        // sufficient, since a native version bump changes at least one of the two sizes in
        // practice, which invalidates a stale cache entry instead of silently reusing it.
        string cacheKey = $"{rid}-{exeInfo.Length:x}-{libInfo.Length:x}";
        string cacheDir = Path.Combine(cacheRoot, cacheKey);
        string cachedExe = Path.Combine(cacheDir, exeName);
        string cachedLib = Path.Combine(cacheDir, libName);

        if (!(IsUpToDate(cachedExe, exeInfo.Length) && IsUpToDate(cachedLib, libInfo.Length)))
        {
            Directory.CreateDirectory(cacheDir);
            CopyAtomically(packagedSlangcPath, cachedExe);
            CopyAtomically(libPath, cachedLib);
        }

        if (!tryEnsureExecutable(cachedExe))
        {
            throw new UnauthorizedAccessException(
                $"Could not set the execute permission on the cached slangc copy '{cachedExe}'.");
        }
        return cachedExe;
    }

    /// <summary>The bundled RID a resolved slangc path belongs to: the running host's.</summary>
    private static string RidOf(string packagedSlangcPath) =>
        SlangToolPath.CurrentRid ?? throw new PlatformNotSupportedException(
            $"No bundled slangc RID for this host (resolving '{packagedSlangcPath}').");

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

    /// <summary>Writes and deletes a throwaway marker file to test write access.</summary>
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

    private const UnixFileMode ExecuteBits =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>
    /// Makes <paramref name="path"/> executable for its owner, group and others when it is
    /// not already user-executable. Always <see langword="true"/> on Windows (no mode bits).
    /// Returns <see langword="false"/> when the mode cannot be changed (not the owner, or a
    /// read-only file system), so the caller falls back to a cached copy it owns.
    /// </summary>
    internal static bool TryEnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return true;

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & UnixFileMode.UserExecute) != 0)
                return true;

            File.SetUnixFileMode(path, mode | ExecuteBits);
            return (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
