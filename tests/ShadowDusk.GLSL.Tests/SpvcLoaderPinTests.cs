#nullable enable

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Shouldly;
using ShadowDusk.GLSL.Interop;
using Xunit;

namespace ShadowDusk.GLSL.Tests;

/// <summary>
/// <see cref="SpvcLoader"/> loads only the pinned SPIRV-Cross (issue #350). These read the files
/// the build deployed beside this assembly (the Silk.NET.SPIRV.Cross.Native package's
/// <c>runtimes/&lt;rid&gt;/native</c>), so a Silk.NET bump that is not re-pinned in
/// <see cref="SpvcLoader.Sha256ByRid"/> fails here, at build time, instead of refusing every
/// OpenGL compile at a consumer.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SpvcLoaderPinTests
{
    private static string FileNameFor(string rid) =>
        rid.StartsWith("win-", StringComparison.Ordinal) ? "spirv-cross.dll"
        : rid.StartsWith("osx-", StringComparison.Ordinal) ? "libspirv-cross.dylib"
        : "libspirv-cross.so";

    private static string Sha256Of(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact]
    public void EveryPin_IsTheFileTheSilkPackageDeploys()
    {
        foreach ((string rid, string pinned) in SpvcLoader.Sha256ByRid)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", FileNameFor(rid));
            File.Exists(path).ShouldBeTrue($"Silk.NET.SPIRV.Cross.Native {SpvcLoader.PinnedSilkVersion} deployed no {path}");
            Sha256Of(path).ShouldBe(pinned, $"{rid}: {path}");
        }
    }

    /// <summary>
    /// The build-time warning (SD0226, <c>buildTransitive/ShadowDusk.GLSL.targets</c>) and the
    /// package pin must name the same release the runtime hashes belong to.
    /// </summary>
    [Fact]
    public void TheBuildWarningAndThePackagePin_NameThePinnedSilkVersion()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "ShadowDusk.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("repo root not found");

        string targets = File.ReadAllText(Path.Combine(root, "src", "ShadowDusk.GLSL", "buildTransitive", "ShadowDusk.GLSL.targets"));
        targets.ShouldContain(
            $"<_ShadowDuskPinnedSilkSpirvCrossVersion>{SpvcLoader.PinnedSilkVersion}</_ShadowDuskPinnedSilkSpirvCrossVersion>",
            Case.Sensitive);

        string packages = File.ReadAllText(Path.Combine(root, "Directory.Packages.props"));
        packages.ShouldContain(
            $"<PackageVersion Include=\"Silk.NET.SPIRV.Cross.Native\" Version=\"{SpvcLoader.PinnedSilkVersion}\" />",
            Case.Sensitive);
    }

    [Fact]
    public void EveryDeployedSpirvCross_IsPinned()
    {
        string runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        List<string> deployed = Directory.EnumerateFiles(runtimes, "*spirv-cross*", SearchOption.AllDirectories).ToList();
        deployed.ShouldNotBeEmpty();
        foreach (string file in deployed)
        {
            string rid = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file)))!;
            SpvcLoader.Sha256ByRid.ContainsKey(rid).ShouldBeTrue($"{file} ships for a RID with no pin");
        }
    }

    [Fact]
    public void ThisProcess_LoadsThePinnedFile_ByAbsolutePath()
    {
        SpvcLoader.EnsureLoaded().ShouldBeNull();

        string loaded = SpvcLoader.LoadedPath.ShouldNotBeNull();
        Path.IsPathFullyQualified(loaded).ShouldBeTrue(loaded);
        Sha256Of(loaded).ShouldBe(SpvcLoader.Sha256ByRid[SpvcLoader.MapRid(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), OperatingSystem.IsAndroid(), RuntimeInformation.ProcessArchitecture)]);
    }
}
