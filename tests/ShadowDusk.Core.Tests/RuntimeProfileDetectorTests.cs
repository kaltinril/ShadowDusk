#nullable enable

using System.Reflection;
using FluentAssertions;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Pure unit tests for the <see cref="RuntimeProfileDetector"/> runtime-detection advisory
/// (Phase 35 auto-select seam 6).
/// </summary>
public sealed class RuntimeProfileDetectorTests
{
    [Theory]
    [InlineData("nkast.Xna.Framework", DetectedRuntime.Kni)]
    [InlineData("nkast.Xna.Framework.Graphics", DetectedRuntime.Kni)]
    [InlineData("MonoGame.Framework", DetectedRuntime.MonoGame)]
    [InlineData("FNA", DetectedRuntime.Fna)]
    [InlineData("FNA.Core", DetectedRuntime.Fna)]
    [InlineData("Something.Else", DetectedRuntime.Unknown)]
    [InlineData("", DetectedRuntime.Unknown)]
    [InlineData(null, DetectedRuntime.Unknown)]
    public void Classify_MapsAssemblyNameToRuntime(string? assemblyName, DetectedRuntime expected)
    {
        RuntimeProfileDetector.Classify(assemblyName).Should().Be(expected);
    }

    [Fact]
    public void Classify_KniBeforeMonoGame()
    {
        // KNI's assemblies could theoretically share a prefix; ensure the nkast check wins.
        RuntimeProfileDetector.Classify("nkast.Xna.Framework").Should().Be(DetectedRuntime.Kni);
    }

    [Theory]
    [InlineData(DetectedRuntime.MonoGame, PlatformTarget.OpenGL, "MonoGameGL_3_8_2")]
    [InlineData(DetectedRuntime.MonoGame, PlatformTarget.DirectX, "MonoGameDX_SM5")]
    [InlineData(DetectedRuntime.Kni, PlatformTarget.OpenGL, "MonoGameGL_3_8_2")]
    [InlineData(DetectedRuntime.Unknown, PlatformTarget.OpenGL, "MonoGameGL_3_8_2")]
    [InlineData(DetectedRuntime.Fna, PlatformTarget.OpenGL, "Fna_Fx2")]
    [InlineData(DetectedRuntime.MonoGame, PlatformTarget.Fna, "Fna_Fx2")]
    public void Recommend_PicksTheProvenProfile(DetectedRuntime runtime, PlatformTarget target, string expectedProfileName)
    {
        RuntimeProfileDetector.Recommend(runtime, target).Name.Should().Be(expectedProfileName);
    }

    [Fact]
    public void Recommend_IsConservative_NeverAutoSelectsNewerContainers()
    {
        // Until v11/KNIFX are promoted, auto-detect never returns them: it only ever returns the
        // universally-loadable v10 (or fx_2_0) contract, so a consumer is never silently upgraded.
        RuntimeProfileDetector.Recommend(DetectedRuntime.Kni, PlatformTarget.OpenGL)
            .Container.Should().Be(EffectContainer.Mgfx);
        RuntimeProfileDetector.Recommend(DetectedRuntime.MonoGame, PlatformTarget.OpenGL)
            .MgfxVersion.Should().Be(10);
    }

    [Theory]
    [InlineData(DetectedRuntime.MonoGame, PlatformTarget.OpenGL)]
    [InlineData(DetectedRuntime.MonoGame, PlatformTarget.DirectX)]
    [InlineData(DetectedRuntime.Kni, PlatformTarget.OpenGL)]
    [InlineData(DetectedRuntime.Unknown, PlatformTarget.OpenGL)]
    public void Recommend_ProfileBackendMatchesRequestedTarget(DetectedRuntime runtime, PlatformTarget target)
    {
        // Auto-detect stays consistent with Profile-implies-backend: the recommended profile's
        // GraphicsTarget equals the requested target, so passing it as Profile alone compiles to the
        // detected backend (regression guard for the "don't break auto-detect" change).
        RuntimeProfileDetector.Recommend(runtime, target).GraphicsTarget.Should().Be(target);
    }

    [Fact]
    public void Recommend_Fna_AlwaysTargetsFnaBackend()
    {
        RuntimeProfileDetector.Recommend(DetectedRuntime.Fna, PlatformTarget.OpenGL)
            .GraphicsTarget.Should().Be(PlatformTarget.Fna);
    }

    [Fact]
    public void Recommend_FromAssembly_UsesItsSimpleName()
    {
        // The test assembly is not an XNA runtime, so it classifies Unknown -> the v10 default.
        Assembly self = typeof(RuntimeProfileDetectorTests).Assembly;
        RuntimeProfileDetector.Recommend(self, PlatformTarget.OpenGL)
            .Should().Be(CapabilityProfile.MonoGameGL_3_8_2);
    }
}
