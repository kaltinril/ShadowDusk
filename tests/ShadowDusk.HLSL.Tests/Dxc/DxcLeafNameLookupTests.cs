#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Dxc;

/// <summary>
/// Pure tests for <see cref="DxcLeafNameLookup"/> (issue #332): which compiles make DXC's
/// leaf-name load of its own library, where the dynamic linkers that do not match the leaf to
/// the loaded image would look, and the verdict for each answer. No native library, disk or
/// process involved; the integration half (<c>DxcDebugSpirvLeafNameTests</c>) asks the real
/// linkers.
/// </summary>
public sealed class DxcLeafNameLookupTests
{
    private const string Pinned = "/app/runtimes/linux-x64/native/libdxcompiler.so";
    private const string Identity = "deadbeef";

    [Fact]
    public void OnlyASpirvCompileWithDebugInformation_MakesTheLeafNameLoad()
    {
        var debug = new DxcCompileOptions { EmbedDebugInfo = true };

        DxcLeafNameLookup.CompileReadsSourceThroughLeafNameLoad(
            DxcFlagBuilder.Build(PlatformTarget.OpenGL, ShaderStage.Pixel, "PS", [], debug)).ShouldBeTrue(
            "OpenGL with debug information is -spirv -Zi: ReadSourceCode runs");
        DxcLeafNameLookup.CompileReadsSourceThroughLeafNameLoad(
            DxcFlagBuilder.Build(PlatformTarget.Vulkan, ShaderStage.Vertex, "VS", [], debug)).ShouldBeTrue();

        DxcLeafNameLookup.CompileReadsSourceThroughLeafNameLoad(
            DxcFlagBuilder.Build(PlatformTarget.OpenGL, ShaderStage.Pixel, "PS", [])).ShouldBeFalse(
            "a release SPIR-V compile never reaches the SPIR-V emitter's source read");
        DxcLeafNameLookup.CompileReadsSourceThroughLeafNameLoad(
            DxcFlagBuilder.Build(PlatformTarget.DirectX12, ShaderStage.Pixel, "PS", [], debug)).ShouldBeFalse(
            "DXIL debug information is built in-process; only the SPIR-V emitter loads the library");
        DxcLeafNameLookup.CompileReadsSourceThroughLeafNameLoad(DxcFlagBuilder.BuildPreprocess([])).ShouldBeFalse();
    }

    [Fact]
    public void MacSearch_DyldLibraryPathFirst_ThenRpathUsrLibCwd_ThenDefaultFallbacks()
    {
        List<string> paths = DxcLeafNameLookup.MacLeafNameSearchPaths(
            dyldLibraryPath: "/decoy:/other",
            dyldFallbackLibraryPath: null,
            workingDirectory: "/work",
            executableDirectory: "/usr/local/share/dotnet").ToList();

        paths.ShouldBe(
        [
            "/decoy/libdxcompiler.dylib",
            "/other/libdxcompiler.dylib",
            "/usr/local/share/dotnet/../lib/libdxcompiler.dylib",
            "/usr/lib/libdxcompiler.dylib",
            "/work/libdxcompiler.dylib",
            "/usr/local/lib/libdxcompiler.dylib",
        ], "dyld-940/1042 order: DYLD_LIBRARY_PATH, the implicit @rpath expansion of the dylib's " +
           "LC_RPATH @executable_path/../lib, /usr/lib, the working directory, then the default fallbacks " +
           "(/usr/lib already listed)");
    }

    [Fact]
    public void MacSearch_ExplicitFallbackPathReplacesTheDefaults_AndEmptyEntriesAreDropped()
    {
        List<string> paths = DxcLeafNameLookup.MacLeafNameSearchPaths(
            dyldLibraryPath: "::",
            dyldFallbackLibraryPath: "/fallback:",
            workingDirectory: "/work",
            executableDirectory: null).ToList();

        paths.ShouldBe(
        [
            "/usr/lib/libdxcompiler.dylib",
            "/work/libdxcompiler.dylib",
            "/fallback/libdxcompiler.dylib",
        ]);
    }

    [Fact]
    public void LinuxSearch_LdLibraryPathThenTheCallersRunpath()
    {
        List<string> paths = DxcLeafNameLookup.LinuxLeafNameSearchPaths(
            ldLibraryPath: "/opt/vulkan/lib::/decoy",
            pinnedDirectory: "/app/runtimes/linux-x64/native").ToList();

        paths.ShouldBe(
        [
            "/opt/vulkan/lib/libdxcompiler.so",
            "/decoy/libdxcompiler.so",
            "/app/runtimes/linux-x64/native/../lib/libdxcompiler.so",
        ], "glibc tries LD_LIBRARY_PATH, then the calling object's RUNPATH, $ORIGIN/../lib in the pinned build");
    }

    [Fact]
    public void LinuxSearch_NoLdLibraryPath_IsJustTheRunpath()
    {
        DxcLeafNameLookup.LinuxLeafNameSearchPaths(null, "/n").ShouldBe(["/n/../lib/libdxcompiler.so"]);
    }

    [Fact]
    public void Decide_LeafResolvesToThePinnedImage_IsNotAFinding()
    {
        var pinned = new DxcLeafNameLookup.Candidate(Pinned, IsPinnedBuild: true, "the pinned build");

        DxcLeafNameLookup.Decide(pinned, foundUnloadedFile: false, [pinned], Pinned, Identity).ShouldBeNull();
    }

    [Fact]
    public void Decide_LeafResolvesToAForeignLoadedLibrary_RefusesNamingIt()
    {
        var foreign = new DxcLeafNameLookup.Candidate("/host/libdxcompiler.so", IsPinnedBuild: false, "build id 0123");

        ShaderError? error = DxcLeafNameLookup.Decide(foreign, foundUnloadedFile: false, [foreign], Pinned, Identity);

        error.ShouldNotBeNull();
        error.Code.ShouldBe(DxcLeafNameLookup.ErrorCode);
        error.Message.ShouldContain("/host/libdxcompiler.so", Case.Sensitive);
        error.Message.ShouldContain("already loaded", Case.Sensitive);
        error.Message.ShouldContain(Pinned, Case.Sensitive);
        error.Message.ShouldContain("Release compiles never make this load", Case.Sensitive);
    }

    [Fact]
    public void Decide_Miss_ForeignFileWhereTheSearchLooks_RefusesNamingEveryForeignFile()
    {
        var copy = new DxcLeafNameLookup.Candidate("/copies/libdxcompiler.so", IsPinnedBuild: true, "the pinned build");
        var sdk = new DxcLeafNameLookup.Candidate("/opt/vulkan/lib/libdxcompiler.so", IsPinnedBuild: false, "build id 9999");
        var other = new DxcLeafNameLookup.Candidate("/decoy/libdxcompiler.so", IsPinnedBuild: false, "not a DXC library");

        ShaderError? error = DxcLeafNameLookup.Decide(null, foundUnloadedFile: true, [copy, sdk, other], Pinned, Identity);

        error.ShouldNotBeNull();
        error.Code.ShouldBe("SD0223");
        error.Message.ShouldContain("/opt/vulkan/lib/libdxcompiler.so", Case.Sensitive);
        error.Message.ShouldContain("/decoy/libdxcompiler.so", Case.Sensitive);
        error.Message.ShouldNotContain("/copies/", Case.Sensitive);
        error.Message.ShouldContain("loaded and initialized inside the compile", Case.Sensitive);
    }

    [Fact]
    public void Decide_Miss_OnlyCopiesOfThePinnedBuildWhereTheSearchLooks_IsNotAFinding()
    {
        var copy = new DxcLeafNameLookup.Candidate("/copies/libdxcompiler.so", IsPinnedBuild: true, "the pinned build");

        DxcLeafNameLookup.Decide(null, foundUnloadedFile: true, [copy], Pinned, Identity).ShouldBeNull(
            "the same build through another path is the same compiler, the rule DxcLoader applies at load time");
        DxcLeafNameLookup.Decide(null, foundUnloadedFile: false, [copy], Pinned, Identity).ShouldBeNull();
    }

    [Fact]
    public void Decide_Miss_GlibcFoundAFileShadowDuskCouldNotLocate_RefusesAndSaysWhereToLook()
    {
        ShaderError? error = DxcLeafNameLookup.Decide(null, foundUnloadedFile: true, [], Pinned, Identity);

        error.ShouldNotBeNull();
        error.Code.ShouldBe("SD0223");
        error.Message.ShouldContain("ld.so.cache", Case.Sensitive);
        error.Message.ShouldContain("ldconfig -p", Case.Sensitive);
    }

    [Fact]
    public void Decide_Miss_NothingAnywhere_IsNotAFinding()
    {
        DxcLeafNameLookup.Decide(null, foundUnloadedFile: false, [], Pinned, Identity).ShouldBeNull(
            "DXC's own load fails, as it does today, and the emitter falls back to the in-memory source");
    }
}
