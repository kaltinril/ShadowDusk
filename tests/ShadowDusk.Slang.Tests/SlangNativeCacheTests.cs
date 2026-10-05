#nullable enable

using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Tests for <see cref="SlangNativeCache"/>: plain file-system operations against temp
/// directories, no process spawned, so these are not Integration-tagged (matching this
/// project's convention that "no disk, no process" pure tests mean no NATIVE PROCESS, not
/// literally zero file I/O — c.f. <c>ShadowDusk.Compiler.Tests.EffectCompilerTests</c>'s own
/// fixture-file reads in its non-Integration-path helpers).
///
/// <para>The "unwritable directory" cases use <see cref="SlangNativeCache"/>'s internal
/// <c>isWritable</c> seam rather than the real OS probe: Windows' directory <c>ReadOnly</c>
/// attribute does NOT actually block creating files inside the directory (verified
/// empirically) — there is no cheap, portable way to make a temp directory genuinely
/// unwritable from a test. Every test passes its own cache root, so nothing is written into
/// the real per-user cache. The real execute-bit probe
/// (<see cref="SlangNativeCache.TryEnsureExecutable"/>) is exercised directly against real
/// files below.</para>
/// </summary>
public sealed class SlangNativeCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sd-slang-cache-test-{Guid.NewGuid():N}");
    private string CacheRoot => Path.Combine(_root, "cache");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    public static TheoryData<string> Rids() => ["win-x64", "linux-x64", "osx-x64", "osx-arm64"];

    private (string PackagedDir, string SlangcPath) MakePackagedNative(
        string rid = "win-x64", byte[]? exeBytes = null, byte[]? libBytes = null)
    {
        string dir = Path.Combine(_root, $"packaged-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string exePath = Path.Combine(dir, SlangToolPath.ExecutableFileName(rid));
        File.WriteAllBytes(exePath, exeBytes ?? [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, SlangToolPath.CompilerLibraryFileName(rid)), libBytes ?? [4, 5, 6, 7]);
        return (dir, exePath);
    }

    private static readonly Func<string, bool> AlwaysWritable = _ => true;
    private static readonly Func<string, bool> NeverWritable = _ => false;
    private static readonly Func<string, bool> AlwaysExecutable = _ => true;

    private string Run(string slangcPath, string rid, Func<string, bool> isWritable, Func<string, bool>? tryExec = null) =>
        SlangNativeCache.EnsureRunnableSlangc(slangcPath, rid, isWritable, tryExec ?? AlwaysExecutable, CacheRoot);

    [Theory]
    [MemberData(nameof(Rids))]
    public void WritableAndExecutable_ReturnsThePackagedExecutableAsIs_NoCopy(string rid)
    {
        (_, string slangcPath) = MakePackagedNative(rid);

        string result = Run(slangcPath, rid, AlwaysWritable);

        result.ShouldBe(slangcPath);
        Directory.Exists(CacheRoot).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Rids))]
    public void Unwritable_CopiesBothFilesIntoARidKeyedCacheDirectory(string rid)
    {
        (_, string slangcPath) = MakePackagedNative(rid);

        string result = Run(slangcPath, rid, NeverWritable);

        string cacheDir = Path.GetDirectoryName(result)!;
        Path.GetDirectoryName(cacheDir).ShouldBe(CacheRoot);
        Path.GetFileName(cacheDir).ShouldStartWith($"{rid}-", Case.Sensitive);
        Path.GetFileName(result).ShouldBe(SlangToolPath.ExecutableFileName(rid));
        File.ReadAllBytes(result).ShouldBe(File.ReadAllBytes(slangcPath));
        File.Exists(Path.Combine(cacheDir, SlangToolPath.CompilerLibraryFileName(rid))).ShouldBeTrue();
    }

    [Fact]
    public void WritableButCannotBeMadeExecutable_FallsBackToTheCachedCopy()
    {
        // A writable directory holding a file this user cannot chmod (another owner) must
        // not be used as-is: the copy we own is made executable instead.
        (_, string slangcPath) = MakePackagedNative("linux-x64");
        var execAttempts = new List<string>();
        bool TryExec(string p)
        {
            execAttempts.Add(p);
            return p != slangcPath;
        }

        string result = Run(slangcPath, "linux-x64", AlwaysWritable, TryExec);

        result.ShouldNotBe(slangcPath);
        execAttempts.ShouldBe([slangcPath, result]);
    }

    [Fact]
    public void CachedCopyCannotBeMadeExecutable_Throws()
    {
        (_, string slangcPath) = MakePackagedNative("osx-arm64");

        Should.Throw<UnauthorizedAccessException>(() => Run(slangcPath, "osx-arm64", NeverWritable, _ => false));
    }

    [Fact]
    public void Unwritable_SecondCallReusesTheCache_NoRecopy()
    {
        (_, string slangcPath) = MakePackagedNative();

        string first = Run(slangcPath, "win-x64", NeverWritable);
        DateTime firstWriteTime = File.GetLastWriteTimeUtc(first);

        string second = Run(slangcPath, "win-x64", NeverWritable);

        second.ShouldBe(first);
        File.GetLastWriteTimeUtc(second).ShouldBe(firstWriteTime);
    }

    [Fact]
    public void Unwritable_DifferentFileSizes_GetDifferentCacheDirectories()
    {
        (_, string slangcPathV1) = MakePackagedNative(exeBytes: [1, 2, 3]);
        (_, string slangcPathV2) = MakePackagedNative(exeBytes: [1, 2, 3, 4, 5]); // different length

        string v1 = Run(slangcPathV1, "win-x64", NeverWritable);
        string v2 = Run(slangcPathV2, "win-x64", NeverWritable);

        v1.ShouldNotBe(v2, "a native version bump (different byte length) must not reuse a stale cache entry");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingCompilerLibrary_ThrowsFileNotFound_WhetherOrNotTheDirectoryIsWritable(bool writable)
    {
        // A partial restore must be reported up front, not as slangc's own cryptic loader
        // failure at its first run.
        string dir = Path.Combine(_root, "broken");
        Directory.CreateDirectory(dir);
        string slangcPath = Path.Combine(dir, "slangc");
        File.WriteAllBytes(slangcPath, [1]);

        var ex = Should.Throw<FileNotFoundException>(
            () => Run(slangcPath, "osx-arm64", writable ? AlwaysWritable : NeverWritable));
        ex.FileName.ShouldBe(Path.Combine(dir, SlangToolPath.CompilerLibraryFileName("osx-arm64")));
    }

    [Fact]
    public void MissingExecutable_ThrowsFileNotFound()
    {
        string slangcPath = Path.Combine(_root, "nowhere", "slangc");

        Should.Throw<FileNotFoundException>(() => Run(slangcPath, "linux-x64", AlwaysWritable));
    }

    // ------------------------------------------------- the real execute-bit probe

    [Fact]
    public void TryEnsureExecutable_SetsTheExecuteBits_OnANonExecutableFile()
    {
        // A copy that lost its execute bit (or was extracted by another user) must be fixed.
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "slangc");
        File.WriteAllBytes(path, [1]);

        if (OperatingSystem.IsWindows())
        {
            // No mode bits on Windows: the probe is a constant true there.
            SlangNativeCache.TryEnsureExecutable(path).ShouldBeTrue();
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        SlangNativeCache.TryEnsureExecutable(path).ShouldBeTrue();

        UnixFileMode mode = File.GetUnixFileMode(path);
        (mode & UnixFileMode.UserExecute).ShouldBe(UnixFileMode.UserExecute);
        (mode & UnixFileMode.GroupExecute).ShouldBe(UnixFileMode.GroupExecute);
        (mode & UnixFileMode.OtherExecute).ShouldBe(UnixFileMode.OtherExecute);
        (mode & UnixFileMode.UserRead).ShouldBe(UnixFileMode.UserRead);
    }

    [Fact]
    public void TryEnsureExecutable_MissingFile_IsFalseOffWindows()
    {
        string path = Path.Combine(_root, "absent-slangc");

        SlangNativeCache.TryEnsureExecutable(path).ShouldBe(OperatingSystem.IsWindows());
    }
}
