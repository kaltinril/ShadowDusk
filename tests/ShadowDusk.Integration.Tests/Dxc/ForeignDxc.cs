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
