#nullable enable

using System.Runtime.InteropServices;
using Shouldly;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Pure tests for <see cref="PinnedNativeLibrary"/>'s probe order (issue #350): vkd3d-shader and
/// SPIRV-Cross are loaded only by absolute path from the places ShadowDusk's packages deliver
/// them, never by a bare name the OS search path answers.
/// </summary>
public sealed class PinnedNativeLibraryTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    [Fact]
    public void CandidatePaths_AreAllAbsolute_NeverABareName()
    {
        List<string> candidates = PinnedNativeLibrary.CandidatePaths(
                P("app"), [P("plugin")], [P("nuget", "pkg", "runtimes", "linux-x64", "native"), P("app")],
                "linux-x64", ["libspirv-cross.so"], ignoreCase: false)
            .ToList();

        candidates.ShouldNotBeEmpty();
        candidates.ShouldAllBe(c => Path.IsPathFullyQualified(c));
        candidates.ShouldAllBe(c => Path.GetFileName(c) == "libspirv-cross.so");
    }

    [Fact]
    public void CandidatePaths_BesideTheAssembliesFirst_ThenTheApp_ThenTheHostSearchDirectories()
    {
        string search = P("nuget", "silk", "runtimes", "osx-arm64", "native");
        List<string> candidates = PinnedNativeLibrary.CandidatePaths(
                P("app"), [P("plugin")], [search], "osx-arm64", ["lib.dylib"], ignoreCase: false)
            .ToList();

        candidates.ShouldBe(
        [
            // A plugin host (MGCB): its base directory and deps.json know nothing about us.
            P("plugin", "runtimes", "osx-arm64", "native", "lib.dylib"),
            P("plugin", "osx-arm64", "lib.dylib"),
            P("plugin", "lib.dylib"),
            P("app", "runtimes", "osx-arm64", "native", "lib.dylib"),
            P("app", "osx-arm64", "lib.dylib"),
            P("app", "lib.dylib"),
            // A single-file bundle's extraction keeps the per-arch subdirectory.
            Path.Combine(search, "osx-arm64", "lib.dylib"),
            Path.Combine(search, "lib.dylib"),
        ]);
    }

    [Fact]
    public void CandidatePaths_AnAssemblyLoadedFromANuGetLibFolder_ProbesThatPackagesRuntimes()
    {
        string lib = P("nuget", "shadowdusk.hlsl", "1.0.0", "lib", "net8.0");
        List<string> candidates = PinnedNativeLibrary.CandidatePaths(
                P("app"), [lib], [], "win-x64", ["libvkd3d-shader-1.dll"], ignoreCase: true)
            .ToList();

        candidates.ShouldContain(P("nuget", "shadowdusk.hlsl", "1.0.0", "runtimes", "win-x64", "native", "libvkd3d-shader-1.dll"));
    }

    [Fact]
    public void CandidatePaths_ProbeEachDirectoryOnce_EveryFileNameInOrder()
    {
        List<string> candidates = PinnedNativeLibrary.CandidatePaths(
                P("app"), [P("app"), P("app") + Path.DirectorySeparatorChar], [P("app")],
                "linux-x64", ["libvkd3d-shader.so.1", "libvkd3d-shader.so"], ignoreCase: false)
            .ToList();

        candidates.ShouldBe(candidates.Distinct().ToList(), "a directory was probed twice");
        candidates.IndexOf(P("app", "libvkd3d-shader.so.1"))
            .ShouldBe(candidates.IndexOf(P("app", "libvkd3d-shader.so")) - 1);
    }

    [Theory]
    [InlineData(true, false, false, Architecture.X64, "win-x64")]
    [InlineData(true, false, false, Architecture.Arm64, "win-arm64")]
    [InlineData(true, false, false, Architecture.X86, "win-x86")]
    [InlineData(false, true, false, Architecture.X64, "osx-x64")]
    [InlineData(false, true, false, Architecture.Arm64, "osx-arm64")]
    [InlineData(false, false, true, Architecture.Arm64, "android-arm64")]
    [InlineData(false, false, false, Architecture.X64, "linux-x64")]
    [InlineData(false, false, false, Architecture.Arm64, "linux-arm64")]
    [InlineData(false, false, false, Architecture.Arm, "linux-arm")]
    public void MapRid_NamesTheProcessArchitecture(bool windows, bool osx, bool android, Architecture arch, string expected) =>
        PinnedNativeLibrary.MapRid(windows, osx, android, arch).ShouldBe(expected);
}
