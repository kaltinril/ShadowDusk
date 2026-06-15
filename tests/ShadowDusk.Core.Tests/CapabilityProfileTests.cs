#nullable enable

using System.Reflection;
using FluentAssertions;
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
        CapabilityProfile.MonoGameGL_3_8_2.Dialect.Should().Be(ShaderDialect.LegacyMojoShader);
        CapabilityProfile.MonoGameDX_SM5.Dialect.Should().Be(ShaderDialect.NotApplicable);
        CapabilityProfile.MonoGameGL_3_8_5.Dialect.Should().Be(ShaderDialect.LegacyMojoShader);
        CapabilityProfile.KniGL_4_02.Dialect.Should().Be(ShaderDialect.LegacyMojoShader);
        CapabilityProfile.Fna_Fx2.Dialect.Should().Be(ShaderDialect.NotApplicable);
    }

    [Fact]
    public void ProvenProfiles_SpanTheContainerFormats()
    {
        // The closed set expresses every (runtime, format) cell: MGFX v10, MGFX v11, and KNIFX v11.
        CapabilityProfile.MonoGameGL_3_8_2.Container.Should().Be(EffectContainer.Mgfx);
        CapabilityProfile.MonoGameGL_3_8_2.MgfxVersion.Should().Be(10);

        CapabilityProfile.MonoGameGL_3_8_5.Container.Should().Be(EffectContainer.Mgfx);
        CapabilityProfile.MonoGameGL_3_8_5.MgfxVersion.Should().Be(11);

        CapabilityProfile.KniGL_4_02.Container.Should().Be(EffectContainer.Knifx);
    }

    [Fact]
    public void ProvenProfiles_DeclareTheirGraphicsBackend()
    {
        // A profile fully specifies its output target, so it carries the backend too.
        CapabilityProfile.MonoGameGL_3_8_2.GraphicsTarget.Should().Be(PlatformTarget.OpenGL);
        CapabilityProfile.MonoGameDX_SM5.GraphicsTarget.Should().Be(PlatformTarget.DirectX);
        CapabilityProfile.MonoGameGL_3_8_5.GraphicsTarget.Should().Be(PlatformTarget.OpenGL);
        CapabilityProfile.KniGL_4_02.GraphicsTarget.Should().Be(PlatformTarget.OpenGL);
        CapabilityProfile.Fna_Fx2.GraphicsTarget.Should().Be(PlatformTarget.Fna);
    }

    [Fact]
    public void IsAClosedSet_NoPublicOrInternalConstructor()
    {
        // The model forbids anonymous combinations: the only way to obtain a profile is the static
        // proven members. Guard that no constructor is reachable to invent an unproven tuple.
        typeof(CapabilityProfile)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().OnlyContain(c => c.IsPrivate,
                "CapabilityProfile is a closed set; only the static proven members may exist");
    }

    [Fact]
    public void EveryProvenProfile_DeclaresNoFeatures()
    {
        // No shipping runtime consumes the lifted GL features yet, so every proven profile must
        // declare None. A profile with a feature is only valid once that feature is render-proven
        // (and added to ShaderFeatureSupport.RuntimeSupported).
        ProvenProfiles.Should().OnlyContain(p => p.AllowedFeatures == ShaderFeatures.None);
    }

    [Fact]
    public void NoProvenProfile_SelectsModernGlsl()
    {
        // ModernGlsl is reserved (no shipping runtime consumes it), so no proven profile may select
        // it. This invariant is what keeps "give the newest experience" from emitting unloadable bytes.
        ProvenProfiles.Should().NotContain(p => p.Dialect == ShaderDialect.ModernGlsl);
    }

    [Fact]
    public void ToString_ReturnsName()
    {
        CapabilityProfile.MonoGameGL_3_8_2.ToString().Should().Be("MonoGameGL_3_8_2");
        CapabilityProfile.MonoGameDX_SM5.ToString().Should().Be("MonoGameDX_SM5");
        CapabilityProfile.MonoGameGL_3_8_5.ToString().Should().Be("MonoGameGL_3_8_5");
        CapabilityProfile.KniGL_4_02.ToString().Should().Be("KniGL_4_02");
        CapabilityProfile.Fna_Fx2.ToString().Should().Be("Fna_Fx2");
    }
}
