#nullable enable

using System.Runtime.InteropServices;
using Shouldly;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// Builds "a DXC that is not ShadowDusk's pinned build" for the issue-#270 tests, from files
/// every test host already has. Three kinds, because each exposes a different failure:
/// <list type="bullet">
/// <item><see cref="PlaceNonDxc"/>: a loadable library that is not DXC at all
///   (<c>spirv-cross</c>) under DXC's file names. Code that loads it dies at
///   <c>DxcCreateInstance</c>, so a loader that prefers it cannot produce the right bytes.</item>
/// <item><see cref="PlaceWorkingForeignBuild"/> (Linux, macOS): a copy of the pinned library
///   with its stamped build id changed (the ELF GNU build id, the Mach-O <c>LC_UUID</c>). On
///   Linux it is still a complete, working DXC, so code that loads it compiles with it without
///   a word: the silent substitution itself. (On Apple silicon the edit invalidates the ad-hoc
///   code signature, so it would not load; the loader must reject it before trying.)</item>
/// <item><see cref="CopyWindowsSdkPair"/>: the real foreign pair, where it is installed.</item>
/// </list>
/// </summary>
internal static class ForeignDxc
{
    /// <summary>The RID of the natives the running process loads.</summary>
    public static string Rid => DxcLoader.PinnedRid(
        OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux",
        RuntimeInformation.ProcessArchitecture);

    /// <summary>The NuGet natives directory beside the test assembly for <paramref name="rid"/>.</summary>
    public static string NativeDirectory(string rid) =>
        Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native");

    /// <summary>
    /// Writes a library that is not DXC into <paramref name="directory"/> under each of
    /// <paramref name="fileNames"/>, replacing what is there.
    /// </summary>
    public static void PlaceNonDxc(IEnumerable<string> fileNames, string directory)
    {
        string source = Path.Combine(
            NativeDirectory(Rid),
            OperatingSystem.IsWindows() ? "spirv-cross.dll"
            : OperatingSystem.IsMacOS() ? "libspirv-cross.dylib"
            : "libspirv-cross.so");
        File.Exists(source).ShouldBeTrue($"cannot build the decoy: {source} is not beside the test assembly");

        Directory.CreateDirectory(directory);
        foreach (string name in fileNames)
            File.Copy(source, Path.Combine(directory, name), overwrite: true);
    }

    /// <summary>
    /// Linux and macOS: replaces <paramref name="pinnedLibrary"/> IN PLACE with a copy of
    /// itself whose stamped build id differs by one byte, and proves the result no longer
    /// reads as the pinned build.
    /// </summary>
    public static void PlaceWorkingForeignBuild(string pinnedLibrary)
    {
        IReadOnlyList<string> identities = DxcNativeIdentity.Read(pinnedLibrary);
        identities.Count.ShouldBe(1, $"{pinnedLibrary} carries no single build id to restamp");

        byte[] image = File.ReadAllBytes(pinnedLibrary);
        int at = image.AsSpan().IndexOf(Convert.FromHexString(identities[0]));
        at.ShouldBeGreaterThanOrEqualTo(0, $"build id {identities[0]} not found in {pinnedLibrary}");
        image[at] ^= 0xFF;
        File.WriteAllBytes(pinnedLibrary, image);

        DxcNativeIdentity.Read(pinnedLibrary).ShouldNotContain(identities[0],
            "the restamped copy still reads as the pinned build, so it is not a foreign DXC");
    }

    /// <summary>
    /// The directory holding DXC 1.9.2602.17 for <paramref name="rid"/>: the natives of
    /// Vortice.Dxc.Native 1.0.5, which is what Vortice.Dxc 3.8.3 pulls in. A real, working DXC
    /// of another build, i.e. exactly what a consumer gets when another package raises
    /// Vortice.Dxc above 3.3.4 (measured with Evergine.DirectX12). The test project downloads
    /// it and copies it under <c>foreign-dxc/</c>; none ships for macOS.
    /// </summary>
    public static string Dxc19Directory(string rid) =>
        Path.Combine(AppContext.BaseDirectory, "foreign-dxc", "1.9", rid, "native");

    /// <summary>
    /// The failure message for a DXC 1.9 file that is not beside the test assembly (issue #362):
    /// names the file looked for AND the NuGet-cache directory the build copied it from, which is
    /// where a custom <c>NUGET_PACKAGES</c> / <c>globalPackagesFolder</c> once made the glob miss.
    /// </summary>
    public static string MissingFixture(string expectedFile)
    {
        string? probed = typeof(ForeignDxc).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ForeignDxcPackageRuntimes")?.Value;
        string source = string.IsNullOrEmpty(probed)
            ? "the build had no NuGetPackageRoot, so nothing was copied"
            : $"the build copies it from {probed}{Rid}/native/ (Vortice.Dxc.Native 1.0.5, a PackageDownload of this project)";
        return $"the DXC 1.9 fixture is missing: {expectedFile}; {source}. Restore and rebuild "
            + "ShadowDusk.Integration.Tests, and check that directory exists in the NuGet global-packages folder.";
    }

    /// <summary>
    /// Copies DXC 1.9 (see <see cref="Dxc19Directory"/>) for the running RID into
    /// <paramref name="directory"/> under ShadowDusk's DXC file names, replacing what is there.
    /// </summary>
    public static void PlaceDxc19(IEnumerable<string> fileNames, string directory)
    {
        string source = Dxc19Directory(Rid);
        Directory.CreateDirectory(directory);
        foreach (string name in fileNames)
        {
            string from = Path.Combine(source, name);
            File.Exists(from).ShouldBeTrue(MissingFixture(from));
            File.Copy(from, Path.Combine(directory, name), overwrite: true);
        }
    }

    /// <summary>
    /// Copies the Windows SDK's own <c>dxil.dll</c> + <c>dxcompiler.dll</c> (the 1.8 pair every
    /// VS Developer Command Prompt has on <c>PATH</c>) into <paramref name="directory"/>.
    /// </summary>
    public static void CopyWindowsSdkPair(string directory)
    {
        string sdk = WindowsDllSearch.FindWindowsSdkBinWithDxil()
            ?? throw new InvalidOperationException(WindowsDllSearch.NoWindowsSdk);

        Directory.CreateDirectory(directory);
        foreach (string name in new[] { "dxcompiler.dll", "dxil.dll" })
        {
            string source = Path.Combine(sdk, name);
            File.Exists(source).ShouldBeTrue($"the Windows SDK directory {sdk} has no {name}");
            File.Copy(source, Path.Combine(directory, name), overwrite: true);
        }
    }
}
