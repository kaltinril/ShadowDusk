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
/// <c>isWritable</c> test seam rather than the real OS probe: Windows' directory
/// <c>ReadOnly</c> attribute does NOT actually block creating files inside the directory
/// (verified empirically) — there is no cheap, portable way to make a temp directory
/// genuinely unwritable from a test without OS-level ACLs, which would be slow and require
/// elevated permissions in CI. The real (production) writability probe itself is exercised
/// implicitly by every other <c>SlangCompilerTests</c> Integration test, since the repo's
/// own <c>tools/slang/win-x64/</c> restore directory IS actually writable.</para>
/// </summary>
public sealed class SlangNativeCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sd-slang-cache-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private (string PackagedDir, string SlangcPath) MakePackagedNative(byte[]? exeBytes = null, byte[]? dllBytes = null)
    {
        string dir = Path.Combine(_root, $"packaged-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string exePath = Path.Combine(dir, "slangc.exe");
        File.WriteAllBytes(exePath, exeBytes ?? [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "slang-compiler.dll"), dllBytes ?? [4, 5, 6, 7]);
        return (dir, exePath);
    }

    private static readonly Func<string, bool> AlwaysWritable = _ => true;
    private static readonly Func<string, bool> NeverWritable = _ => false;

    [Fact]
    public void WritableProbe_ReturnsPackagedDirectoryAsIs_NoCopy()
    {
        (string packagedDir, string slangcPath) = MakePackagedNative();

        string result = SlangNativeCache.EnsureWritableToolDirectory(slangcPath, AlwaysWritable);

        result.ShouldBe(packagedDir);
        // No cache directory created for the writable path.
        Directory.Exists(Path.Combine(_root, "cache")).ShouldBeFalse();
    }

    [Fact]
    public void UnwritableProbe_CopiesIntoACacheDirectory()
    {
        (_, string slangcPath) = MakePackagedNative();

        string result = SlangNativeCache.EnsureWritableToolDirectory(slangcPath, NeverWritable);

        File.Exists(Path.Combine(result, "slangc.exe")).ShouldBeTrue();
        File.Exists(Path.Combine(result, "slang-compiler.dll")).ShouldBeTrue();
        File.ReadAllBytes(Path.Combine(result, "slangc.exe")).ShouldBe(File.ReadAllBytes(slangcPath));
    }

    [Fact]
    public void UnwritableProbe_SecondCallReusesTheCache_NoRecopy()
    {
        (_, string slangcPath) = MakePackagedNative();

        string first = SlangNativeCache.EnsureWritableToolDirectory(slangcPath, NeverWritable);
        DateTime firstWriteTime = File.GetLastWriteTimeUtc(Path.Combine(first, "slangc.exe"));

        string second = SlangNativeCache.EnsureWritableToolDirectory(slangcPath, NeverWritable);

        second.ShouldBe(first);
        File.GetLastWriteTimeUtc(Path.Combine(second, "slangc.exe")).ShouldBe(firstWriteTime);
    }

    [Fact]
    public void UnwritableProbe_DifferentFileSizes_GetDifferentCacheDirectories()
    {
        (_, string slangcPathV1) = MakePackagedNative(exeBytes: [1, 2, 3]);
        (_, string slangcPathV2) = MakePackagedNative(exeBytes: [1, 2, 3, 4, 5]); // different length

        string v1 = SlangNativeCache.EnsureWritableToolDirectory(slangcPathV1, NeverWritable);
        string v2 = SlangNativeCache.EnsureWritableToolDirectory(slangcPathV2, NeverWritable);

        v1.ShouldNotBe(v2, "a native version bump (different byte length) must not reuse a stale cache entry");
    }

    [Fact]
    public void UnwritableProbe_MissingCompilerDll_ThrowsFileNotFoundException()
    {
        string dir = Path.Combine(_root, "broken");
        Directory.CreateDirectory(dir);
        string slangcPath = Path.Combine(dir, "slangc.exe");
        File.WriteAllBytes(slangcPath, [1]);
        // slang-compiler.dll deliberately not written.

        Should.Throw<FileNotFoundException>(
            () => SlangNativeCache.EnsureWritableToolDirectory(slangcPath, NeverWritable));
    }
}
