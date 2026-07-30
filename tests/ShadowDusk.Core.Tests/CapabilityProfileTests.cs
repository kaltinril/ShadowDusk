#nullable enable

using System.Reflection;
using Shouldly;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Pure unit tests for the <see cref="CapabilityProfile"/> closed-set model (Phase 35 auto-select
/// seam 1). No compilation, no I/O.
/// </summary>
public sealed class CapabilityProfileTests
{
    private static readonly CapabilityProfile[] ProvenProfiles =
    {
        CapabilityProfile.MonoGameGL_3_8_2,
        CapabilityProfile.MonoGameDX_SM5,
        CapabilityProfile.MonoGameGL_3_8_5,
        CapabilityProfile.KniGL_4_02,
        CapabilityProfile.Fna_Fx2,
    };

    [Fact]
    public void ProvenProfiles_HaveExpectedDialects()
    {
        CapabilityProfile.MonoGameGL_3_8_2.Dialect.ShouldBe(ShaderDialect.LegacyMojoShader);
        CapabilityProfile.MonoGameDX_SM5.Dialect.ShouldBe(ShaderDialect.NotApplicable);
        CapabilityProfile.MonoGameGL_3_8_5.Dialect.ShouldBe(ShaderDialect.LegacyMojoShader);
        CapabilityProfile.KniGL_4_02.Dialect.ShouldBe(ShaderDialect.LegacyMojoShader);
        CapabilityProfile.Fna_Fx2.Dialect.ShouldBe(ShaderDialect.NotApplicable);
    }

    [Fact]
    public void ProvenProfiles_SpanTheContainerFormats()
    {
        // The closed set expresses every (runtime, format) cell: MGFX v10, MGFX v11, and KNIFX v11.
        CapabilityProfile.MonoGameGL_3_8_2.Container.ShouldBe(EffectContainer.Mgfx);
        CapabilityProfile.MonoGameGL_3_8_2.MgfxVersion.ShouldBe(10);

        CapabilityProfile.MonoGameGL_3_8_5.Container.ShouldBe(EffectContainer.Mgfx);
        CapabilityProfile.MonoGameGL_3_8_5.MgfxVersion.ShouldBe(11);

        CapabilityProfile.KniGL_4_02.Container.ShouldBe(EffectContainer.Knifx);
    }

    [Fact]
    public void ProvenProfiles_DeclareTheirGraphicsBackend()
    {
        // A profile fully specifies its output target, so it carries the backend too.
        CapabilityProfile.MonoGameGL_3_8_2.GraphicsTarget.ShouldBe(PlatformTarget.OpenGL);
        CapabilityProfile.MonoGameDX_SM5.GraphicsTarget.ShouldBe(PlatformTarget.DirectX);
        CapabilityProfile.MonoGameGL_3_8_5.GraphicsTarget.ShouldBe(PlatformTarget.OpenGL);
        CapabilityProfile.KniGL_4_02.GraphicsTarget.ShouldBe(PlatformTarget.OpenGL);
        CapabilityProfile.Fna_Fx2.GraphicsTarget.ShouldBe(PlatformTarget.Fna);
    }

    [Fact]
    public void IsAClosedSet_NoPublicOrInternalConstructor()
    {
        // The model forbids anonymous combinations: the only way to obtain a profile is the static
        // proven members. Guard that no constructor is reachable to invent an unproven tuple.
        typeof(CapabilityProfile)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .ShouldAllBe(c => c.IsPrivate, customMessage: "CapabilityProfile is a closed set; only the static proven members may exist");
    }

    [Fact]
    public void EveryProvenProfile_DeclaresNoFeatures()
    {
        // No shipping runtime consumes the lifted GL features yet, so every proven profile must
        // declare None. A profile with a feature is only valid once that feature is render-proven
        // (and added to ShaderFeatureSupport.RuntimeSupported).
        ProvenProfiles.ShouldAllBe(p => p.AllowedFeatures == ShaderFeatures.None);
    }

    [Fact]
    public void NoProvenProfile_SelectsModernGlsl()
    {
        // ModernGlsl is reserved (no shipping runtime consumes it), so no proven profile may select
        // it. This invariant is what keeps "give the newest experience" from emitting unloadable bytes.
        ProvenProfiles.ShouldNotContain(p => p.Dialect == ShaderDialect.ModernGlsl);
    }

    [Fact]
    public void ToString_ReturnsName()
    {
        CapabilityProfile.MonoGameGL_3_8_2.ToString().ShouldBe("MonoGameGL_3_8_2");
        CapabilityProfile.MonoGameDX_SM5.ToString().ShouldBe("MonoGameDX_SM5");
        CapabilityProfile.MonoGameGL_3_8_5.ToString().ShouldBe("MonoGameGL_3_8_5");
        CapabilityProfile.KniGL_4_02.ToString().ShouldBe("KniGL_4_02");
        CapabilityProfile.Fna_Fx2.ToString().ShouldBe("Fna_Fx2");
    }
}
