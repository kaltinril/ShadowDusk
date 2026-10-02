#nullable enable

using System.Runtime.InteropServices;
using Shouldly;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Dxc;

/// <summary>
/// Pure tests for <see cref="DxcLoader.GetProbeCandidates"/> — the ordered probe
/// paths the macOS DXC resolver tries before the host's native search directories
/// (Phase 37 A). No native library or disk access involved; the candidate
/// generation is pure path arithmetic, so the contract (per-arch first, publish
/// layout, tools/dxc walk-up to the root) is asserted directly.
/// </summary>
public sealed class DxcLoaderTests
{
    // Platform-agnostic inputs: the generator itself is pure and must behave
    // identically regardless of the OS the test runs on.
    private static readonly string Base =
        Path.Combine(Path.GetTempPath(), "repo", "tests", "bin", "Debug", "net8.0");

    [Theory]
    [InlineData(Architecture.Arm64, "osx-arm64")]
    [InlineData(Architecture.X64, "osx-x64")]
    public void FirstCandidate_IsPerArchSubdirNextToAppBinaries(
        Architecture arch, string expectedRid)
    {
        var candidates = DxcLoader.GetProbeCandidates(Base, arch).ToList();

        candidates[0].ShouldBe(
            Path.Combine(Base, expectedRid, DxcLoader.MacLibFileName), customMessage: "the csproj copies the restored dylib to a per-arch subdir next to the binaries");
    }

    [Fact]
    public void SecondCandidate_IsFlatBaseDirectory()
    {
        var candidates = DxcLoader.GetProbeCandidates(Base, Architecture.Arm64).ToList();

        candidates[1].ShouldBe(
            Path.Combine(Base, DxcLoader.MacLibFileName), customMessage: "a manually-placed flat dylib next to the binaries must still win over tools/dxc");
    }

    [Fact]
    public void ThirdCandidate_IsSelfContainedPublishRuntimesLayout()
    {
        var candidates = DxcLoader.GetProbeCandidates(Base, Architecture.X64).ToList();

        candidates[2].ShouldBe(
            Path.Combine(Base, "runtimes", "osx-x64", "native", DxcLoader.MacLibFileName), customMessage: "self-contained publish keeps the NuGet runtimes/<rid>/native layout under the app base");
    }

    [Fact]
    public void ToolsDxcCandidates_WalkUpToTheFilesystemRoot_PerArchBeforeFlat()
    {
        var candidates = DxcLoader.GetProbeCandidates(Base, Architecture.Arm64).ToList();
        var toolsCandidates = candidates.Skip(3).ToList();

        // One per-arch + one flat candidate per ancestor (including the base dir itself).
        int ancestorCount = 0;
        for (DirectoryInfo? dir = new(Base); dir is not null; dir = dir.Parent)
            ancestorCount++;
        toolsCandidates.Count().ShouldBe(ancestorCount * 2);

        // The nearest ancestor is probed first (bin-adjacent tools/ beats repo-root tools/),
        // and within each ancestor the per-arch subdir beats the flat path.
        toolsCandidates[0].ShouldBe(
            Path.Combine(Base, "tools", "dxc", "osx-arm64", DxcLoader.MacLibFileName));
        toolsCandidates[1].ShouldBe(
            Path.Combine(Base, "tools", "dxc", DxcLoader.MacLibFileName));

        string? parent = new DirectoryInfo(Base).Parent!.FullName;
        toolsCandidates[2].ShouldBe(
            Path.Combine(parent, "tools", "dxc", "osx-arm64", DxcLoader.MacLibFileName));
    }

    [Fact]
    public void Candidates_AreLazyAndDistinct()
    {
        var candidates = DxcLoader.GetProbeCandidates(Base, Architecture.X64).ToList();

        candidates.ShouldBeUnique("duplicate probes waste dlopen attempts");
        candidates.ShouldAllBe(
            c => c.EndsWith(DxcLoader.MacLibFileName), customMessage: "every candidate must target the macOS dylib file name");
    }

    [Theory]
    [InlineData(Architecture.Arm64, "osx-arm64")]
    [InlineData(Architecture.X64, "osx-x64")]
    public void SearchDirectoryCandidates_ProbePerArchSubdirBeforeFlat(
        Architecture arch, string expectedRid)
    {
        // Bug-hunt 2026-07-27 C3: the csproj Links the dylib into a per-arch subdir
        // and single-file extraction preserves that relative path, so each host
        // native-search directory (which includes the extraction dir) must be probed
        // per-arch first, then flat. A flat-only probe never sees the extracted dylib
        // and the released macOS archives could not load their own bundled natives.
        string dir = Path.Combine(Path.GetTempPath(), "extract");

        var candidates = DxcLoader.GetSearchDirectoryCandidates(dir, arch).ToList();

        candidates.ShouldBe(new[] {
            Path.Combine(dir, expectedRid, DxcLoader.MacLibFileName),
            Path.Combine(dir, DxcLoader.MacLibFileName)});
    }

    [Fact]
    public void LibraryNames_MatchTheVorticePinvokeAndOurShippedDylib()
    {
        // Load-bearing constants: the Vortice.Dxc P/Invokes declare "dxcompiler.dll"
        // on every OS, and our macOS build ships the plain (unversioned) dylib name
        // the workflow stages and tools/restore.* place.
        DxcLoader.DxcLibraryName.ShouldBe("dxcompiler.dll");
        DxcLoader.MacLibFileName.ShouldBe("libdxcompiler.dylib");
    }

    [Fact]
    public void AndroidLibName_IsTheBareSonameTheApkLinkerResolves()
    {
        // Phase 50: on Android the resolver bare-loads this SONAME from the APK's
        // lib/<abi>/ dir (never the desktop path-probing). It must match the file name
        // tools/restore.* places and ShadowDusk.HLSL.csproj packs under
        // runtimes/android-arm64/native.
        DxcLoader.AndroidLibFileName.ShouldBe("libdxcompiler.so");
    }

    [Fact]
    public void PinnedPairDirectories_ProbeBesideTheAssembliesBeforeTheHostsDirectories()
    {
        // Issue #270: a plugin host's own directories can hold a different DXC (measured: the
        // Windows SDK's pair in <mgcb>/runtimes/win-x64/native won over ShadowDusk's own and
        // compiled DirectX 12 silently). The natives that ship beside the ShadowDusk/Vortice
        // assemblies come first; the host's base and search directories follow.
        string search = Path.Combine(Path.GetTempPath(), "search");
        string plugin = Path.Combine(Path.GetTempPath(), "plugin");

        var dirs = DxcLoader.GetPinnedPairDirectories(
            Base, [search], [plugin], "win-x64", ignoreCase: true).ToList();

        dirs.ShouldBe(new[] {
            Path.Combine(plugin, "runtimes", "win-x64", "native"),
            plugin,
            Path.Combine(Base, "runtimes", "win-x64", "native"),
            Base,
            search});
    }

    [Fact]
    public void PinnedPairDirectories_AssemblyLoadedFromAPackageFolder_ProbeThatPackagesRuntimes()
    {
        // An assembly loaded straight from the NuGet cache sits in <package>/lib/<tfm>/ and its
        // natives in <package>/runtimes/<rid>/native: the pair that ships with it, wherever
        // the host's own directories point.
        string package = Path.Combine(Path.GetTempPath(), "packages", "vortice.dxc", "3.3.4");
        string lib = Path.Combine(package, "lib", "net8.0");

        var dirs = DxcLoader.GetPinnedPairDirectories(
            Base, [], [lib], "win-x64", ignoreCase: true).ToList();

        dirs.ShouldBe(new[] {
            Path.Combine(lib, "runtimes", "win-x64", "native"),
            lib,
            Path.Combine(package, "runtimes", "win-x64", "native"),
            Path.Combine(Base, "runtimes", "win-x64", "native"),
            Base});
    }

    [Fact]
    public void MacCandidates_ProbeBesideTheAssembliesThenTheBaseThenTheSearchDirectories()
    {
        // Same preference as Windows/Linux, with the per-arch subdirectories the macOS layout
        // needs. Every candidate is an absolute path to the dylib: a bare name would be the
        // dynamic linker's search path (DYLD_LIBRARY_PATH, the working directory,
        // /usr/local/lib), exactly where a foreign DXC sits (issue #270).
        string search = Path.Combine(Path.GetTempPath(), "search");
        string plugin = Path.Combine(Path.GetTempPath(), "plugin");
        string lib = DxcLoader.MacLibFileName;

        var candidates = DxcLoader.GetMacCandidates(Base, [search], [plugin], Architecture.Arm64).ToList();

        candidates.Take(6).ShouldBe(new[] {
            Path.Combine(plugin, "osx-arm64", lib),
            Path.Combine(plugin, lib),
            Path.Combine(plugin, "runtimes", "osx-arm64", "native", lib),
            Path.Combine(Base, "osx-arm64", lib),
            Path.Combine(Base, lib),
            Path.Combine(Base, "runtimes", "osx-arm64", "native", lib)});
        candidates.TakeLast(2).ShouldBe(new[] {
            Path.Combine(search, "osx-arm64", lib),
            Path.Combine(search, lib)});
        candidates.ShouldAllBe(c => Path.IsPathFullyQualified(c) && Path.GetFileName(c) == lib);
        candidates.ShouldBeUnique();
    }

    [Fact]
    public void MacCandidates_AppWhoseAssembliesSitInTheBaseDirectory_KeepTheLongStandingOrder()
    {
        // The ordinary app: assemblies and base directory are the same place, so the list is
        // the pre-#270 one (per-arch, flat, runtimes/<rid>/native, tools/dxc walk-up, search
        // directories) with nothing probed twice.
        string search = Path.Combine(Path.GetTempPath(), "search");

        var candidates = DxcLoader.GetMacCandidates(Base, [search], [Base], Architecture.X64).ToList();

        candidates.ShouldBe(
            DxcLoader.GetProbeCandidates(Base, Architecture.X64)
                .Concat(DxcLoader.GetSearchDirectoryCandidates(search, Architecture.X64))
                .ToList());
    }

    [Fact]
    public void PinnedPairDirectories_DropDuplicatesAndTrailingSeparators()
    {
        // The host lists the app directory among its search directories, and the ShadowDusk
        // and Vortice assemblies usually share it: each directory is probed once.
        var dirs = DxcLoader.GetPinnedPairDirectories(
            Base,
            [Base + Path.DirectorySeparatorChar],
            [Base, Base],
            "linux-x64",
            ignoreCase: false).ToList();

        dirs.ShouldBe(new[] {
            Path.Combine(Base, "runtimes", "linux-x64", "native"),
            Base});
    }

    [Theory]
    [InlineData("win", Architecture.X64, "win-x64")]
    [InlineData("win", Architecture.Arm64, "win-arm64")]
    [InlineData("linux", Architecture.X64, "linux-x64")]
    [InlineData("linux", Architecture.Arm64, "linux-arm64")]
    [InlineData("osx", Architecture.X64, "osx-x64")]
    [InlineData("osx", Architecture.Arm64, "osx-arm64")]
    public void PinnedRid_FollowsTheProcessArchitecture(string os, Architecture arch, string expected)
    {
        DxcLoader.PinnedRid(os, arch).ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"C:\app\dxil.dll", @"c:\APP\DXIL.DLL", true)]
    [InlineData(@"\\?\C:\app\dxil.dll", @"C:\app\dxil.dll", true)]
    [InlineData(@"C:\app\dxil.dll", @"C:\other\dxil.dll", false)]
    public void SamePath_IgnoresCaseAndTheLongPathPrefix(string a, string b, bool expected)
    {
        // GetModuleFileNameW reports a \\?\ path when the module was loaded through one, so
        // the pinned dxil.dll reached that way must still be recognized as the pinned file.
        DxcLoader.SamePath(a, b).ShouldBe(expected);
    }

    [Theory]
    [InlineData("libdxil.dylib", true)]
    [InlineData("libdxil.so", true)]
    [InlineData("LIBDXIL.DYLIB", true)]
    [InlineData("libdxcompiler.dylib", false)]
    [InlineData("libdxil.dylib.bak", false)]
    public void DxilLeafNames_AreExactlyTheValidatorNamesDxcOpens(string leaf, bool expected)
    {
        // Our macOS libdxcompiler.dylib dlopens both of these by leaf name; it ships neither,
        // so any loaded image with one of these names is a foreign validator (SD0219).
        DxcLoader.IsDxilLeafName(leaf).ShouldBe(expected);
    }

    [Theory]
    [InlineData(ShadowDusk.Core.PlatformTarget.DirectX12, false, true)]
    [InlineData(ShadowDusk.Core.PlatformTarget.DirectX, true, false)]
    [InlineData(ShadowDusk.Core.PlatformTarget.OpenGL, false, false)]
    [InlineData(ShadowDusk.Core.PlatformTarget.Vulkan, false, false)]
    public void OnlyValidatedDxilCompiles_DependOnTheValidator(
        ShadowDusk.Core.PlatformTarget target, bool skipValidation, bool expected)
    {
        // A foreign dxil.dll loaded by the host refuses only these compiles (SD0219): SPIR-V
        // codegen and -Vd DXIL never call the validator, so they keep compiling.
        var args = DxcFlagBuilder.Build(
            target,
            ShadowDusk.Core.ShaderStage.Pixel,
            "PS",
            [],
            new DxcCompileOptions { SkipValidation = skipValidation });

        DxcShaderCompiler.UsesValidator(args).ShouldBe(expected);
    }

    [Fact]
    public void PinnedVorticeDxc_ExposesTheResolverFieldSubscribeFirstRewrites()
    {
        // DxcLoader.SubscribeFirst puts our resolver ahead of Vortice's own by rewriting this
        // private event field, and silently falls back to a plain (last-in-line) subscription
        // when it is missing, which reopens the Linux LD_LIBRARY_PATH hole. A Vortice.Dxc bump
        // that renames or retypes the field must fail here, not in a consumer's process.
        var field = typeof(Vortice.Dxc.Dxc).GetField(
            "ResolveLibrary",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        field.ShouldNotBeNull();
        field.FieldType.ShouldBe(typeof(DllImportResolver));
    }
}
