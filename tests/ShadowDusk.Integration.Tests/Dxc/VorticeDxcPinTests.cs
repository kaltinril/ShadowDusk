#nullable enable

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Shouldly;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.Integration.Tests.Tests;
using Xunit;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// Issues #282 and #289: the Vortice.Dxc pin is stated in three places that must agree, and the
/// DXC the process really maps is the pinned one, read from the mapped image itself.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VorticeDxcPinTests
{
    /// <summary>
    /// <c>Directory.Packages.props</c> (the exact range the package depends on),
    /// <c>buildTransitive/ShadowDusk.HLSL.targets</c> (the SD0220 build warning) and
    /// <c>DxcNativeIdentity.PinnedVorticeDxcVersion</c> (the SD0219 runtime check) must name the
    /// same release. If they drift, a correct consumer graph gets a false warning or refusal, or
    /// a wrong one gets none.
    /// </summary>
    [Fact]
    public void TheThreePinsAgree_AndTheBoundAssemblyIsThatRelease()
    {
        string root = FindRepoRoot();
        string pinned = DxcNativeIdentity.PinnedVorticeDxcVersion.ToString(3);

        string props = File.ReadAllText(Path.Combine(root, "Directory.Packages.props"));
        Match range = Regex.Match(props, @"<PackageVersion\s+Include=""Vortice\.Dxc""\s+Version=""\[(?<v>[^\]]+)\]""");
        range.Success.ShouldBeTrue("Directory.Packages.props must pin Vortice.Dxc to an exact range [x.y.z]");
        range.Groups["v"].Value.ShouldBe(pinned);

        string targets = File.ReadAllText(Path.Combine(root, "src", "ShadowDusk.HLSL", "buildTransitive", "ShadowDusk.HLSL.targets"));
        Match warning = Regex.Match(targets, @"<_ShadowDuskPinnedVorticeDxcVersion>(?<v>[^<]+)</");
        warning.Success.ShouldBeTrue("the buildTransitive targets must state the pinned Vortice.Dxc version");
        warning.Groups["v"].Value.ShouldBe(pinned);

        typeof(Vortice.Dxc.Dxc).Assembly.GetName().Version!.ToString(3).ShouldBe(pinned);
    }

    /// <summary>
    /// The image the OS reports for DXC's <c>DxcCreateInstance</c> after
    /// <see cref="DxcLoader.Register"/> is a file carrying the pinned identity. On Linux the
    /// build id is ALSO read from the mapped image's note segment (the reader Android depends
    /// on, issue #289) and must agree with the file's.
    /// </summary>
    [DxcFact]
    public void TheMappedDxcImage_IsThePinnedBuild()
    {
        DxcLoader.Register().ShouldBeNull();
        string rid = ForeignDxc.Rid;
        string pinned = DxcNativeIdentity.Expected(rid, DxcNativeKind.Compiler)!;

        string? mapped = DxcLoader.MappedDxcImagePath();
        mapped.ShouldNotBeNull("the OS could not say which DXC image is mapped");
        DxcNativeIdentity.Read(mapped).ShouldContain(pinned, $"mapped DXC image {mapped}");

        if (!OperatingSystem.IsLinux())
            return;

        NativeLibrary.TryLoad(mapped, out IntPtr handle).ShouldBeTrue();
        DxcLoader.LoadedImages.ElfImage? image = DxcLoader.LoadedImages.ElfImageOf(handle, "DxcCreateInstance");
        image.ShouldNotBeNull("dl_iterate_phdr did not find the image holding DxcCreateInstance");
        image.Value.Path.ShouldBe(mapped);
        image.Value.BuildId.ShouldBe(pinned, "the build id read from memory is not the one read from the file");

        // Negative control: another library's image is told apart, so a match above is not the
        // reader returning the same answer for everything.
        string spirvCross = Path.Combine(ForeignDxc.NativeDirectory(rid), "libspirv-cross.so");
        NativeLibrary.TryLoad(spirvCross, out IntPtr other).ShouldBeTrue();
        NativeLibrary.TryGetExport(other, "spvc_context_create", out IntPtr address).ShouldBeTrue();
        DxcLoader.LoadedImages.ElfImage? otherImage = DxcLoader.LoadedImages.ElfImageContaining(address);
        otherImage.ShouldNotBeNull();
        otherImage.Value.Path.ShouldEndWith("libspirv-cross.so", Case.Sensitive);
        otherImage.Value.BuildId.ShouldNotBe(pinned);
    }

    /// <summary>
    /// The android-arm64 <c>libdxcompiler.so</c> ShadowDusk.HLSL packs (copied beside the build
    /// output by its csproj) carries the build id <see cref="DxcNativeIdentity"/> pins, which
    /// is what the on-device check compares the mapped image against. Read from the file here;
    /// a re-pinned native without the matching pin would refuse every on-device compile.
    /// </summary>
    [RestoredAndroidDxcFact]
    public void AndroidArm64Native_CarriesThePinnedBuildId()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "android-arm64", "libdxcompiler.so");
        string pinned = DxcNativeIdentity.Expected("android-arm64", DxcNativeKind.Compiler)!;

        DxcNativeIdentity.Read(path).ShouldContain(pinned,
            $"{path} is {DxcNativeIdentity.Describe(path)}; pin that in DxcNativeIdentity if the " +
            "native was re-pinned on purpose, otherwise the wrong native was restored");
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }
}

/// <summary>
/// A fact over the restored android-arm64 DXC (<c>tools/restore.*</c>). Skipped, never passed,
/// where it has not been restored, unless <c>SHADOWDUSK_REQUIRE_DXC</c> is set (CI): then it
/// runs and fails.
/// </summary>
public sealed class RestoredAndroidDxcFactAttribute : FactAttribute
{
    public RestoredAndroidDxcFactAttribute()
    {
        bool restored = File.Exists(Path.Combine(AppContext.BaseDirectory, "android-arm64", "libdxcompiler.so"));
        if (ShadowDusk.Tests.Shared.NativeRequirement.ShouldSkip(
                restored,
                Environment.GetEnvironmentVariable(ShadowDusk.Tests.Shared.NativeRequirement.DxcEnvVar)))
        {
            Skip = DxcTestGate.SkipReason;
        }
    }
}
