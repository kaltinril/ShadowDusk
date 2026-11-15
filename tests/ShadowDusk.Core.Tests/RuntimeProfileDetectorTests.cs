#nullable enable

using System.Reflection;
using Shouldly;
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
    // What KNI's assemblies are really named (nkast.Xna.Framework* 4.2.9001 and 4.3.9001).
    [InlineData("Xna.Framework", DetectedRuntime.Kni)]
    [InlineData("Xna.Framework.Graphics", DetectedRuntime.Kni)]
    [InlineData("Xna.Framework.Game", DetectedRuntime.Kni)]
    // Real XNA, and a name that only shares the prefix.
    [InlineData("Microsoft.Xna.Framework", DetectedRuntime.Unknown)]
    [InlineData("Xna.FrameworkExtras", DetectedRuntime.Unknown)]
    [InlineData("MonoGame.Framework", DetectedRuntime.MonoGame)]
    [InlineData("FNA", DetectedRuntime.Fna)]
    [InlineData("FNA.Core", DetectedRuntime.Fna)]
    [InlineData("Something.Else", DetectedRuntime.Unknown)]
    [InlineData("", DetectedRuntime.Unknown)]
    [InlineData(null, DetectedRuntime.Unknown)]
    public void Classify_MapsAssemblyNameToRuntime(string? assemblyName, DetectedRuntime expected)
    {
        RuntimeProfileDetector.Classify(assemblyName).ShouldBe(expected);
    }

    [Fact]
    public void Classify_KniBeforeMonoGame()
    {
        // KNI's assemblies could theoretically share a prefix; ensure the nkast check wins.
        RuntimeProfileDetector.Classify("nkast.Xna.Framework").ShouldBe(DetectedRuntime.Kni);
    }

    [Theory]
    [InlineData("4.2.9001.0", "KniGL_4_02", 11)]
    [InlineData("4.2.0.0", "KniGL_4_02", 11)]
    [InlineData("4.0.9001.0", "KniGL_4_02", 11)]
    [InlineData("4.3.9001.0", "KniGL_4_03", 12)]
    [InlineData("4.3.0.0", "KniGL_4_03", 12)]
    [InlineData("4.4.9001.0", "KniGL_4_03", 12)]
    [InlineData("5.0.0.0", "KniGL_4_03", 12)]
    public void RecommendKnifx_PicksTheContainerVersionThatKniLoads(string kniVersion, string expectedProfile, short expectedKnifxVersion)
    {
        CapabilityProfile profile = RuntimeProfileDetector.RecommendKnifx(new Version(kniVersion));

        profile.Name.ShouldBe(expectedProfile);
        profile.Container.ShouldBe(EffectContainer.Knifx);
        profile.KnifxVersion.ShouldBe(expectedKnifxVersion);
    }

    [Fact]
    public void RecommendKnifx_WithoutAVersion_IsV11_WhichEveryKniLoads()
    {
        RuntimeProfileDetector.RecommendKnifx((Version?)null).ShouldBeSameAs(CapabilityProfile.KniGL_4_02);
    }

    [Fact]
    public void RecommendKnifx_RefusesAnAssemblyThatIsNotKni()
    {
        var ex = Should.Throw<ArgumentException>(() =>
            RuntimeProfileDetector.RecommendKnifx(typeof(RuntimeProfileDetectorTests).Assembly));

        ex.ParamName.ShouldBe("kniAssembly");
        ex.Message.ShouldContain("is not a KNI assembly", Case.Sensitive);
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
        RuntimeProfileDetector.Recommend(runtime, target).Name.ShouldBe(expectedProfileName);
    }

    [Fact]
    public void Recommend_IsConservative_NeverAutoSelectsNewerContainers()
    {
        // Until v11/KNIFX are promoted, auto-detect never returns them: it only ever returns the
        // universally-loadable v10 (or fx_2_0) contract, so a consumer is never silently upgraded.
        RuntimeProfileDetector.Recommend(DetectedRuntime.Kni, PlatformTarget.OpenGL)
            .Container.ShouldBe(EffectContainer.Mgfx);
        RuntimeProfileDetector.Recommend(DetectedRuntime.MonoGame, PlatformTarget.OpenGL)
            .MgfxVersion.ShouldBe(10);
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
        RuntimeProfileDetector.Recommend(runtime, target).GraphicsTarget.ShouldBe(target);
    }

    [Fact]
    public void Recommend_Fna_AlwaysTargetsFnaBackend()
    {
        RuntimeProfileDetector.Recommend(DetectedRuntime.Fna, PlatformTarget.OpenGL)
            .GraphicsTarget.ShouldBe(PlatformTarget.Fna);
    }

    [Fact]
    public void Recommend_FromAssembly_UsesItsSimpleName()
    {
        // The test assembly is not an XNA runtime, so it classifies Unknown -> the v10 default.
        Assembly self = typeof(RuntimeProfileDetectorTests).Assembly;
        RuntimeProfileDetector.Recommend(self, PlatformTarget.OpenGL)
            .ShouldBe(CapabilityProfile.MonoGameGL_3_8_2);
    }
}
