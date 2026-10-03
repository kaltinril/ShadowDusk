#nullable enable

using System.Security.Cryptography;
using Shouldly;
using ShadowDusk.HLSL.Vkd3d;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Vkd3d;

/// <summary>
/// <see cref="Vkd3dLoader"/> loads only the pinned vkd3d-shader (issue #350). Its SHA-256 pins
/// must be exactly the ones <c>tools/restore.ps1</c> and <c>tools/restore.sh</c> verify, and the
/// files the build copies beside this assembly; a re-pinned restore that forgets the loader would
/// otherwise refuse every DirectX 11 and FNA compile at a consumer.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Vkd3dLoaderTests
{
    private static string Sha256Of(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Theory]
    [InlineData("restore.ps1")]
    [InlineData("restore.sh")]
    public void EveryPin_IsTheHashTheRestoreScriptVerifies(string script)
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "tools", script));
        foreach ((string rid, string pinned) in Vkd3dLoader.Sha256ByRid)
            text.ShouldContain(pinned, Case.Sensitive, $"{script} does not pin {rid}'s vkd3d-shader as {pinned}");

        Vkd3dLoader.Sha256ByRid.Count.ShouldBe(4, "win-x64, linux-x64, osx-x64 and osx-arm64 ship a vkd3d-shader");
    }

    [Theory]
    [InlineData("win-x64", "libvkd3d-shader-1.dll")]
    [InlineData("linux-x64", "libvkd3d-shader.so.1")]
    [InlineData("osx-x64", "osx-x64/libvkd3d-shader.1.dylib")]
    [InlineData("osx-arm64", "osx-arm64/libvkd3d-shader.1.dylib")]
    public void TheBuildOutputCopy_IsThePinnedFile(string rid, string relativePath)
    {
        string path = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (!File.Exists(path))
            return; // Not restored on this machine; the restore scripts' own hash check covers it.

        Sha256Of(path).ShouldBe(Vkd3dLoader.Sha256ByRid[rid], path);
    }

    [Vkd3dFact]
    public void ThisProcess_LoadsThePinnedFile_ByAbsolutePath()
    {
        Vkd3dLoader.Register().ShouldBeNull();

        string loaded = Vkd3dLoader.LoadedPath.ShouldNotBeNull();
        Path.IsPathFullyQualified(loaded).ShouldBeTrue(loaded);
        Vkd3dLoader.Sha256ByRid.Values.ShouldContain(Sha256Of(loaded), loaded);
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }
}
