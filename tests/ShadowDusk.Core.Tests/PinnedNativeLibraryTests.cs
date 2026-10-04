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

    /// <summary>
    /// dotnet fsi / .NET Interactive load packages in place from the global packages folder, and
    /// SPIRV-Cross ships in Silk.NET.SPIRV.Cross.Native, not ShadowDusk.GLSL: its own package
    /// folder beside ShadowDusk.GLSL's is a candidate (id lowercased, as NuGet lays it out), right
    /// after the loading package's own runtimes folder.
    /// </summary>
    [Fact]
    public void CandidatePaths_AssemblyFromTheGlobalPackagesFolder_ProbesTheNativesOwnPackage()
    {
        string lib = P("nuget", "packages", "shadowdusk.glsl", "1.2.3", "lib", "net8.0");
        List<string> candidates = PinnedNativeLibrary.CandidatePaths(
                P("dotnet", "sdk", "FSharp"), [lib], [], "linux-x64", ["libspirv-cross.so"], ignoreCase: false,
                new NuGetPackageIdentity("Silk.NET.SPIRV.Cross.Native", "2.23.0"))
            .ToList();

        string own = P("nuget", "packages", "shadowdusk.glsl", "1.2.3", "runtimes", "linux-x64", "native", "libspirv-cross.so");
        string silk = P("nuget", "packages", "silk.net.spirv.cross.native", "2.23.0", "runtimes", "linux-x64", "native", "libspirv-cross.so");
        candidates.ShouldContain(silk);
        candidates.IndexOf(silk).ShouldBe(candidates.IndexOf(own) + 1);
    }

    [Fact]
    public void CandidatePaths_NoNativePackage_OrAnAssemblyOutsideAPackage_AddsNoSiblingPackage()
    {
        var silk = new NuGetPackageIdentity("Silk.NET.SPIRV.Cross.Native", "2.23.0");
        PinnedNativeLibrary.CandidatePaths(
                P("app"), [P("nuget", "packages", "shadowdusk.glsl", "1.2.3", "lib", "net8.0")], [],
                "linux-x64", ["libspirv-cross.so"], ignoreCase: false)
            .ShouldNotContain(c => c.Contains("silk.net.spirv.cross.native", StringComparison.Ordinal));
        PinnedNativeLibrary.CandidatePaths(
                P("app"), [P("app", "bin")], [], "linux-x64", ["libspirv-cross.so"], ignoreCase: false, silk)
            .ShouldNotContain(c => c.Contains("silk.net.spirv.cross.native", StringComparison.Ordinal));
    }

    [Fact]
    public void RetryableLoad_RetriesAFailure_AndKeepsTheFirstSuccess()
    {
        int calls = 0;
        var failure = new PinnedNativeLibrary.LoadResult(IntPtr.Zero, null, new ShaderError("", 0, 0, "SD0103", "locked"));
        var success = new PinnedNativeLibrary.LoadResult(new IntPtr(42), "/x/libspirv-cross.so", null);
        var load = new PinnedNativeLibrary.RetryableLoad(() => ++calls == 1 ? failure : success);

        load.Value.ShouldBeSameAs(failure);
        load.Succeeded.ShouldBeNull();
        load.Value.ShouldBeSameAs(success);
        load.Value.ShouldBeSameAs(success);
        load.Succeeded.ShouldBeSameAs(success);
        calls.ShouldBe(2, "a success must be cached, a failure retried");
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
