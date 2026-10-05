#nullable enable

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Direct tests for <see cref="SlangToolPath"/> (issue #225): the RID map, the unsupported-
/// host reasons, the per-RID native file names, every probe in the resolution order and
/// their precedence, the repository-root walk-up's three root markers (and their
/// near-misses), and <see cref="SlangToolPath.ResolveOrThrow()"/>'s failure paths. File
/// system only, against throwaway temp directories (no process), so not Integration-tagged,
/// matching <see cref="SlangNativeCacheTests"/>.
/// </summary>
public sealed class SlangToolPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sd-slang-toolpath-test-{Guid.NewGuid():N}");

    public SlangToolPathTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Dir(params string[] parts)
    {
        string dir = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Touch(string dir, string fileName)
    {
        string path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    // ---------------------------------------------------------------- RID map + support

    [Theory]
    [InlineData(true, false, false, Architecture.X64, "win-x64")]
    [InlineData(false, true, false, Architecture.X64, "linux-x64")]
    [InlineData(false, false, true, Architecture.X64, "osx-x64")]
    [InlineData(false, false, true, Architecture.Arm64, "osx-arm64")]
    public void RidFor_MapsEveryBundledHost(bool win, bool linux, bool mac, Architecture arch, string expected) =>
        SlangToolPath.RidFor(win, linux, mac, arch).ShouldBe(expected);

    [Theory]
    [InlineData(true, false, false, Architecture.Arm64)]   // win-arm64: not bundled
    [InlineData(true, false, false, Architecture.X86)]
    [InlineData(false, true, false, Architecture.Arm64)]   // linux-arm64: not bundled (no vkd3d/DXC either)
    [InlineData(false, false, false, Architecture.Arm64)]  // Android/iOS/browser: IsLinux() is false on Android
    [InlineData(false, false, false, Architecture.Wasm)]
    public void RidFor_UnbundledHost_IsNull(bool win, bool linux, bool mac, Architecture arch) =>
        SlangToolPath.RidFor(win, linux, mac, arch).ShouldBeNull();

    [Fact]
    public void UnsupportedReason_UnbundledRid_NamesTheHostAndTheBundledRids()
    {
        string? reason = SlangToolPath.GetUnsupportedReason(null, isMacOS: false, new Version(6, 1), "linux-arm64");

        reason.ShouldNotBeNull();
        reason.ShouldContain("linux-arm64", Case.Sensitive);
        foreach (string rid in SlangToolPath.SupportedRids)
            reason.ShouldContain(rid, Case.Sensitive);
    }

    [Fact]
    public void UnsupportedReason_WinArm64_NamesTheRidAndAsksForAnIssue()
    {
        // Issue #286: the core pipeline is measured on win-arm64, but its slangc is not bundled
        // (size). A win-arm64 consumer must get a reason naming its RID and the way to ask.
        string? reason = SlangToolPath.GetUnsupportedReason(null, isMacOS: false, new Version(10, 0), "win-arm64");

        reason.ShouldNotBeNull();
        reason.ShouldContain("this host (win-arm64) is not one of them", Case.Sensitive);
        reason.ShouldContain("If you need ShadowDusk.Slang on win-arm64", Case.Sensitive);
        reason.ShouldContain("https://github.com/kaltinril/ShadowDusk/issues", Case.Sensitive);
        SlangToolPath.SupportedRids.ShouldNotContain("win-arm64");
    }

    [Theory]
    [InlineData(12, 0)]
    [InlineData(15, 7)]
    [InlineData(25, 9)]
    public void UnsupportedReason_MacOSOlderThan26_IsUnsupported(int major, int minor)
    {
        string? reason = SlangToolPath.GetUnsupportedReason(
            "osx-arm64", isMacOS: true, new Version(major, minor), "osx-arm64");

        reason.ShouldNotBeNull();
        reason.ShouldContain("macOS 26", Case.Sensitive);
        reason.ShouldContain($"{major}.{minor}", Case.Sensitive);
    }

    [Theory]
    [InlineData("osx-arm64", true, 26, 0)]
    [InlineData("osx-x64", true, 27, 1)]
    [InlineData("win-x64", false, 10, 0)]   // Windows/Linux kernel versions are never compared to the macOS floor
    [InlineData("linux-x64", false, 5, 15)]
    public void UnsupportedReason_SupportedHost_IsNull(string rid, bool isMacOS, int major, int minor) =>
        SlangToolPath.GetUnsupportedReason(rid, isMacOS, new Version(major, minor), rid).ShouldBeNull();

    [Fact]
    public void ThisHost_IsSupported_AndResolvesTheRestoredNative_WhenItIsABundledRid()
    {
        // The hosts CI and the developer boxes run on are all bundled RIDs (and the macOS
        // runner image is macOS 26). If this ever runs somewhere unbundled the assertion
        // below names it instead of passing vacuously.
        string? rid = SlangToolPath.CurrentRid;
        rid.ShouldNotBeNull($"this test host ({RuntimeInformation.RuntimeIdentifier}) is not a bundled slangc RID");
        SlangToolPath.GetUnsupportedReason().ShouldBeNull();
        SlangToolPath.IsSupportedOnThisPlatform.ShouldBeTrue();
    }

    // ------------------------------------------------------------------ file names

    [Theory]
    [InlineData("win-x64", "slangc.exe", "slang-compiler.dll")]
    [InlineData("linux-x64", "slangc", "libslang-compiler.so.0." + SlangToolPath.SlangVersion)]
    [InlineData("osx-x64", "slangc", "libslang-compiler.0." + SlangToolPath.SlangVersion + ".dylib")]
    [InlineData("osx-arm64", "slangc", "libslang-compiler.0." + SlangToolPath.SlangVersion + ".dylib")]
    public void NativeFileNames_PerRid(string rid, string exe, string lib)
    {
        SlangToolPath.ExecutableFileName(rid).ShouldBe(exe);
        SlangToolPath.CompilerLibraryFileName(rid).ShouldBe(lib);
    }

    [Fact]
    public void CompilerLibraryFileName_UnbundledRid_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => SlangToolPath.CompilerLibraryFileName("linux-arm64"));

    // ---------------------------------------------------------------- probe order

    [Fact]
    public void Resolve_BaseDirectoryHit()
    {
        string baseDir = Dir("app");
        string exe = Touch(baseDir, "slangc");

        SlangToolPath.Resolve("linux-x64", baseDir, []).ShouldBe(exe);
    }

    [Fact]
    public void Resolve_RuntimesRidNativeUnderBase_FrameworkDependentConsumerLayout()
    {
        string baseDir = Dir("app");
        string exe = Touch(Dir("app", "runtimes", "osx-arm64", "native"), "slangc");
        // A different RID's copy must never be picked up.
        Touch(Dir("app", "runtimes", "osx-x64", "native"), "slangc");

        SlangToolPath.Resolve("osx-arm64", baseDir, []).ShouldBe(exe);
    }

    [Fact]
    public void Resolve_NativeSearchDirectoryHit_InListOrder()
    {
        string baseDir = Dir("app");
        string empty = Dir("search-empty");
        string first = Dir("search-1");
        string second = Dir("search-2");
        string exe1 = Touch(first, "slangc.exe");
        Touch(second, "slangc.exe");

        SlangToolPath.Resolve("win-x64", baseDir, [empty, first, second]).ShouldBe(exe1);
    }

    [Fact]
    public void Resolve_Precedence_Base_ThenRuntimes_ThenSearchDirs_ThenRepoTools()
    {
        string repo = Dir("repo");
        Touch(repo, "ShadowDusk.slnx");
        string baseDir = Dir("repo", "bin", "app");
        string search = Dir("search");
        string toolsExe = Touch(Dir("repo", "tools", "slang", "linux-x64"), "slangc");

        SlangToolPath.Resolve("linux-x64", baseDir, [search]).ShouldBe(toolsExe);

        string searchExe = Touch(search, "slangc");
        SlangToolPath.Resolve("linux-x64", baseDir, [search]).ShouldBe(searchExe);

        string runtimesExe = Touch(Dir("repo", "bin", "app", "runtimes", "linux-x64", "native"), "slangc");
        SlangToolPath.Resolve("linux-x64", baseDir, [search]).ShouldBe(runtimesExe);

        string baseExe = Touch(baseDir, "slangc");
        SlangToolPath.Resolve("linux-x64", baseDir, [search]).ShouldBe(baseExe);
    }

    [Fact]
    public void Resolve_WrongExecutableNameForTheRid_IsNotAHit()
    {
        // A self-contained win-x64 publish flattens slangc.exe beside the app, and a base
        // directory can hold it on any OS; a Unix RID must look for "slangc", never fall for
        // "slangc.exe".
        string baseDir = Dir("app");
        Touch(baseDir, "slangc.exe");

        SlangToolPath.Resolve("osx-arm64", baseDir, []).ShouldBeNull();
    }

    // ------------------------------------------------- repo-root walk-up, three markers

    [Fact]
    public void RepoWalkUp_SlnxMarker()
    {
        Touch(Dir("repo"), "ShadowDusk.slnx");
        string exe = Touch(Dir("repo", "tools", "slang", "osx-x64"), "slangc");

        SlangToolPath.Resolve("osx-x64", Dir("repo", "tests", "bin", "Debug", "net8.0"), []).ShouldBe(exe);
    }

    [Fact]
    public void RepoWalkUp_GitDirectoryWithObjects()
    {
        Dir("repo", ".git", "objects");
        string exe = Touch(Dir("repo", "tools", "slang", "linux-x64"), "slangc");

        SlangToolPath.Resolve("linux-x64", Dir("repo", "a", "b"), []).ShouldBe(exe);
    }

    [Fact]
    public void RepoWalkUp_GitFile_Worktree()
    {
        File.WriteAllText(Path.Combine(Dir("repo"), ".git"), "gitdir: /somewhere/.git/worktrees/x\n");
        string exe = Touch(Dir("repo", "tools", "slang", "win-x64"), "slangc.exe");

        SlangToolPath.Resolve("win-x64", Dir("repo", "a"), []).ShouldBe(exe);
    }

    [Fact]
    public void RepoWalkUp_GitDirectoryWithoutObjects_IsNotARoot()
    {
        Dir("repo", ".git");   // no objects/: a stray .git folder, not a repository
        Touch(Dir("repo", "tools", "slang", "linux-x64"), "slangc");

        SlangToolPath.Resolve("linux-x64", Dir("repo", "a"), []).ShouldBeNull();
    }

    [Fact]
    public void RepoWalkUp_GitFileWithoutGitdirPrefix_IsNotARoot()
    {
        File.WriteAllText(Path.Combine(Dir("repo"), ".git"), "not a worktree pointer");
        Touch(Dir("repo", "tools", "slang", "linux-x64"), "slangc");

        SlangToolPath.Resolve("linux-x64", Dir("repo", "a"), []).ShouldBeNull();
    }

    [Fact]
    public void RepoWalkUp_ToolsFolderOutsideAnyRepoRoot_IsIgnored()
    {
        // The guard Vkd3dLoader.FindToolsVkd3d also has: an unrelated tools/slang/ on the
        // ancestor walk is never mistaken for the pinned, hash-verified restore.
        Touch(Dir("tools", "slang", "linux-x64"), "slangc");

        SlangToolPath.Resolve("linux-x64", Dir("not-a-repo", "bin"), []).ShouldBeNull();
    }

    [Fact]
    public void RepoWalkUp_FindsTheNearestRootWithTheNative_SkippingRootsWithout()
    {
        // Outer repo has the native; an inner nested checkout (a root marker, no tools/) does
        // not. The walk keeps going past the inner root rather than stopping at it.
        Touch(Dir("outer"), "ShadowDusk.slnx");
        string exe = Touch(Dir("outer", "tools", "slang", "osx-arm64"), "slangc");
        Touch(Dir("outer", "inner"), "ShadowDusk.slnx");

        SlangToolPath.Resolve("osx-arm64", Dir("outer", "inner", "bin"), []).ShouldBe(exe);
    }

    // ----------------------------------------------------------- ResolveOrThrow

    [Fact]
    public void ResolveOrThrow_NotFound_ThrowsFileNotFound_NamingTheRidAndEveryProbe()
    {
        var ex = Should.Throw<FileNotFoundException>(
            () => SlangToolPath.ResolveOrThrow(null, "osx-arm64", Dir("empty-app"), [Dir("empty-search")]));

        ex.FileName.ShouldBe("slangc");
        ex.Message.ShouldContain("osx-arm64", Case.Sensitive);
        ex.Message.ShouldContain("runtimes/osx-arm64/native/", Case.Sensitive);
        ex.Message.ShouldContain("tools/slang/osx-arm64/", Case.Sensitive);
    }

    [Fact]
    public void ResolveOrThrow_UnsupportedHost_ThrowsPlatformNotSupported_WithTheReason()
    {
        var ex = Should.Throw<PlatformNotSupportedException>(
            () => SlangToolPath.ResolveOrThrow("old macOS", "osx-arm64", Dir("app"), []));

        ex.Message.ShouldBe("old macOS");
    }

    [Fact]
    public void ResolveOrThrow_NoRid_ThrowsPlatformNotSupported() =>
        Should.Throw<PlatformNotSupportedException>(
            () => SlangToolPath.ResolveOrThrow(null, null, Dir("app"), []));

    [Fact]
    public void ResolveOrThrow_Found_ReturnsThePath()
    {
        string baseDir = Dir("app");
        string exe = Touch(baseDir, "slangc.exe");

        SlangToolPath.ResolveOrThrow(null, "win-x64", baseDir, []).ShouldBe(exe);
    }

    // ------------------------------------------- the pin, kept in sync everywhere

    [Fact]
    public void SlangVersion_MatchesTheRestoreScriptsAndThePackagePins()
    {
        string repo = FindRepoRoot();

        string sh = File.ReadAllText(Path.Combine(repo, "tools", "restore.sh"));
        Regex.Match(sh, "^SLANG_VERSION=\"([^\"]+)\"", RegexOptions.Multiline).Groups[1].Value
            .ShouldBe(SlangToolPath.SlangVersion);

        string ps1 = File.ReadAllText(Path.Combine(repo, "tools", "restore.ps1"));
        Regex.Match(ps1, @"^\$SlangVersion = '([^']+)'", RegexOptions.Multiline).Groups[1].Value
            .ShouldBe(SlangToolPath.SlangVersion);

        string csproj = File.ReadAllText(Path.Combine(repo, "src", "ShadowDusk.Slang", "ShadowDusk.Slang.csproj"));
        Regex.Match(csproj, "<SlangNativeVersion>([^<]+)</SlangNativeVersion>").Groups[1].Value
            .ShouldBe(SlangToolPath.SlangVersion);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root (ShadowDusk.slnx).");
    }
}
