#nullable enable

using Shouldly;
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
        GlRequirement.IsRequired(value).ShouldBeFalse(
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
        GlRequirement.IsRequired(value).ShouldBeTrue(
            "any set value other than 0/false must make a missing GL context a hard failure");
    }

    [Fact]
    public void BuildFailureMessage_CarriesReasonGateNameAndRemedy()
    {
        string msg = GlRequirement.BuildFailureMessage("GLFW init failed: no display");

        msg.ShouldContain(GlRequirement.EnvVar, Case.Sensitive);
        msg.ShouldContain("GLFW init failed: no display", Case.Sensitive, "the underlying cause must not be swallowed");
        msg.ShouldContain("LIBGL_ALWAYS_SOFTWARE", Case.Sensitive, "the message must point at the working headless recipe");
    }

    [Fact]
    public void BuildFailureMessage_NullReason_StillProducesAMessage()
    {
        string msg = GlRequirement.BuildFailureMessage(null);
        msg.ShouldContain("unknown", Case.Sensitive);
    }

    [Fact]
    public void BuildSoftSkipNotice_IsUnmistakable()
    {
        string notice = GlRequirement.BuildSoftSkipNotice("headless");

        notice.ShouldContain("rendered 0", Case.Sensitive, "a log reader must see that PASS meant no rendering");
        notice.ShouldContain("WITHOUT RENDERING", Case.Sensitive);
        notice.ShouldContain("headless", Case.Sensitive);
        notice.ShouldContain(GlRequirement.EnvVar, Case.Sensitive, "the notice must advertise the hardening switch");
    }
}
