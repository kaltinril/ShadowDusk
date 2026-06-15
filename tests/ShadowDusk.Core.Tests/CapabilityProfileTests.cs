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
        CapabilityProfile.Fna_Fx2,
    };

    [Fact]
    public void ProvenProfiles_HaveExpectedDialects()
    {
        CapabilityProfile.MonoGameGL_3_8_2.Dialect.Should().Be(ShaderDialect.LegacyMojoShader);
        CapabilityProfile.MonoGameDX_SM5.Dialect.Should().Be(ShaderDialect.NotApplicable);
        CapabilityProfile.Fna_Fx2.Dialect.Should().Be(ShaderDialect.NotApplicable);
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
        CapabilityProfile.Fna_Fx2.ToString().Should().Be("Fna_Fx2");
    }
}
