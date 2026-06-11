#nullable enable

using System.Runtime.InteropServices;
using FluentAssertions;
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

        candidates[0].Should().Be(
            Path.Combine(Base, expectedRid, DxcLoader.MacLibFileName),
            "the csproj copies the restored dylib to a per-arch subdir next to the binaries");
    }

    [Fact]
    public void SecondCandidate_IsFlatBaseDirectory()
    {
        var candidates = DxcLoader.GetProbeCandidates(Base, Architecture.Arm64).ToList();

        candidates[1].Should().Be(
            Path.Combine(Base, DxcLoader.MacLibFileName),
            "a manually-placed flat dylib next to the binaries must still win over tools/dxc");
    }

    [Fact]
    public void ThirdCandidate_IsSelfContainedPublishRuntimesLayout()
    {
        var candidates = DxcLoader.GetProbeCandidates(Base, Architecture.X64).ToList();

        candidates[2].Should().Be(
            Path.Combine(Base, "runtimes", "osx-x64", "native", DxcLoader.MacLibFileName),
            "self-contained publish keeps the NuGet runtimes/<rid>/native layout under the app base");
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
        toolsCandidates.Should().HaveCount(ancestorCount * 2);

        // The nearest ancestor is probed first (bin-adjacent tools/ beats repo-root tools/),
        // and within each ancestor the per-arch subdir beats the flat path.
        toolsCandidates[0].Should().Be(
            Path.Combine(Base, "tools", "dxc", "osx-arm64", DxcLoader.MacLibFileName));
        toolsCandidates[1].Should().Be(
            Path.Combine(Base, "tools", "dxc", DxcLoader.MacLibFileName));

        string? parent = new DirectoryInfo(Base).Parent!.FullName;
        toolsCandidates[2].Should().Be(
            Path.Combine(parent, "tools", "dxc", "osx-arm64", DxcLoader.MacLibFileName));
    }

    [Fact]
    public void Candidates_AreLazyAndDistinct()
    {
        var candidates = DxcLoader.GetProbeCandidates(Base, Architecture.X64).ToList();

        candidates.Should().OnlyHaveUniqueItems("duplicate probes waste dlopen attempts");
        candidates.Should().OnlyContain(
            c => c.EndsWith(DxcLoader.MacLibFileName),
            "every candidate must target the macOS dylib file name");
    }

    [Fact]
    public void LibraryNames_MatchTheVorticePinvokeAndOurShippedDylib()
    {
        // Load-bearing constants: the Vortice.Dxc P/Invokes declare "dxcompiler.dll"
        // on every OS, and our macOS build ships the plain (unversioned) dylib name
        // the workflow stages and tools/restore.* place.
        DxcLoader.DxcLibraryName.Should().Be("dxcompiler.dll");
        DxcLoader.MacLibFileName.Should().Be("libdxcompiler.dylib");
    }
}
