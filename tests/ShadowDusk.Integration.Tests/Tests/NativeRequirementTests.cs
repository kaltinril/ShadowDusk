#nullable enable

using FluentAssertions;
using ShadowDusk.Tests.Shared;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Pure unit tests for the <c>SHADOWDUSK_REQUIRE_VKD3D</c> / <c>SHADOWDUSK_REQUIRE_DXC</c>
/// gate semantics — the <c>GlRequirementTests</c> sibling. No environment reads, no
/// native probes, no I/O: deliberately NOT tagged Integration so they run on the fast
/// unit path on every host, including ones where the natives themselves are absent —
/// which is the point: the skip-vs-fail decision logic must be provably correct even
/// where it can't be exercised live.
/// </summary>
public sealed class NativeRequirementTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData(" 0 ")]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public void IsRequired_UnsetOrDisabledValues_AreNotRequired(string? value)
    {
        NativeRequirement.IsRequired(value).Should().BeFalse(
            "unset/empty/0/false must keep the skip-with-reason behavior for local runs");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData(" 1 ")]
    public void IsRequired_SetValues_AreRequired(string value)
    {
        NativeRequirement.IsRequired(value).Should().BeTrue(
            "any set value other than 0/false must make a missing native a hard failure");
    }

    [Fact]
    public void ShouldSkip_NativeAvailable_NeverSkips()
    {
        NativeRequirement.ShouldSkip(nativeAvailable: true, envValue: null).Should().BeFalse();
        NativeRequirement.ShouldSkip(nativeAvailable: true, envValue: "1").Should().BeFalse();
    }

    [Fact]
    public void ShouldSkip_NativeMissing_GateUnset_Skips()
    {
        NativeRequirement.ShouldSkip(nativeAvailable: false, envValue: null).Should().BeTrue(
            "local runs without the restored native must keep the truthful skip");
    }

    [Fact]
    public void ShouldSkip_NativeMissing_GateSet_DoesNotSkip()
    {
        NativeRequirement.ShouldSkip(nativeAvailable: false, envValue: "1").Should().BeFalse(
            "with the gate set, a missing native must let the test RUN and fail loudly " +
            "at the native boundary instead of skipping green (CI restore-failure net)");
    }

    [Fact]
    public void EnvVarNames_AreTheDocumentedGates()
    {
        // ci.yml's integration job sets exactly these names — a rename here without a
        // workflow update would silently disable the CI hard gate.
        NativeRequirement.Vkd3dEnvVar.Should().Be("SHADOWDUSK_REQUIRE_VKD3D");
        NativeRequirement.DxcEnvVar.Should().Be("SHADOWDUSK_REQUIRE_DXC");
    }
}
