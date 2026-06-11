#nullable enable

using FluentAssertions;
using ShadowDusk.ImageTests.GlContext;
using Xunit;

namespace ShadowDusk.ImageTests.Tests;

/// <summary>
/// Pure unit tests for the <c>SHADOWDUSK_REQUIRE_GL</c> gate semantics
/// (Phase 37 tail item 4). No environment reads, no GL, no I/O — these run
/// on every host including ones where the GL fixture itself would skip,
/// which is the point: the gate logic must be provably correct even where
/// it can't be exercised live.
/// </summary>
public sealed class GlRequirementTests
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
        GlRequirement.IsRequired(value).Should().BeFalse(
            "unset/empty/0/false must keep the visible soft-skip behavior");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData(" 1 ")]
    public void IsRequired_SetValues_AreRequired(string value)
    {
        GlRequirement.IsRequired(value).Should().BeTrue(
            "any set value other than 0/false must make a missing GL context a hard failure");
    }

    [Fact]
    public void BuildFailureMessage_CarriesReasonGateNameAndRemedy()
    {
        string msg = GlRequirement.BuildFailureMessage("GLFW init failed: no display");

        msg.Should().Contain(GlRequirement.EnvVar);
        msg.Should().Contain("GLFW init failed: no display", "the underlying cause must not be swallowed");
        msg.Should().Contain("LIBGL_ALWAYS_SOFTWARE", "the message must point at the working headless recipe");
    }

    [Fact]
    public void BuildFailureMessage_NullReason_StillProducesAMessage()
    {
        string msg = GlRequirement.BuildFailureMessage(null);
        msg.Should().Contain("unknown");
    }

    [Fact]
    public void BuildSoftSkipNotice_IsUnmistakable()
    {
        string notice = GlRequirement.BuildSoftSkipNotice("headless");

        notice.Should().Contain("rendered 0", "a log reader must see that PASS meant no rendering");
        notice.Should().Contain("WITHOUT RENDERING");
        notice.Should().Contain("headless");
        notice.Should().Contain(GlRequirement.EnvVar, "the notice must advertise the hardening switch");
    }
}
