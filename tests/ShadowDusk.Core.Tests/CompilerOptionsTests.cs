#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Core.Tests;

public sealed class CompilerOptionsTests
{
    [Fact]
    public void DxbcBackend_DefaultsToVkd3d()
    {
        // The default DXBC backend must be the cross-platform, host-independent
        // vkd3d-shader backend — a bare DirectX compile (and the bare CLI invocation)
        // must work identically on Linux, macOS, and Windows. The Windows-only
        // d3dcompiler_47 oracle is opt-in only.
        new CompilerOptions().DxbcBackend.Should().Be(DxbcBackend.Vkd3d);
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX, true)]
    [InlineData(PlatformTarget.OpenGL,  true)]
    [InlineData(PlatformTarget.Vulkan,  true)]
    [InlineData(PlatformTarget.Fna,     true)]
    [InlineData(PlatformTarget.Metal,   false)]
    public void PlatformMacros_IsSupported_MatchesFor(PlatformTarget target, bool expected)
    {
        PlatformMacros.IsSupported(target).Should().Be(expected);

        // IsSupported is the pre-check for For (no exception-as-control-flow): the two
        // must agree exactly.
        Action act = () => PlatformMacros.For(target);
        if (expected)
            act.Should().NotThrow();
        else
            act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
