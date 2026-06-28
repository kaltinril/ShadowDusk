#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Core.Tests.Preprocessor;

public sealed class PlatformMacrosTests
{
    // -------------------------------------------------------------------------
    // 2.2 — Exact macro names per platform
    // -------------------------------------------------------------------------

    [Fact]
    public void For_DirectX_ReturnsExactlyMgfxHlslSm4()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        macroSet.Macros.Select(m => m.Name)
            .Should().Equal("MGFX", "HLSL", "SM4");
    }

    [Fact]
    public void For_OpenGL_ReturnsExactlyMgfxGlslOpengl()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.OpenGL);

        macroSet.Macros.Select(m => m.Name)
            .Should().Equal("MGFX", "GLSL", "OPENGL");
    }

    [Fact]
    public void For_Vulkan_ReturnsExactlyMgfxHlslVulkanSm6()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.Vulkan);

        macroSet.Macros.Select(m => m.Name)
            .Should().Equal("MGFX", "HLSL", "VULKAN", "SM6");
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX, 3)]
    [InlineData(PlatformTarget.OpenGL, 3)]
    [InlineData(PlatformTarget.Vulkan, 4)]
    public void For_KnownPlatform_HasExpectedMacroCount(PlatformTarget platform, int expectedCount)
    {
        var macroSet = PlatformMacros.For(platform);

        macroSet.Macros.Should().HaveCount(expectedCount);
    }

    // -------------------------------------------------------------------------
    // 2.2b — Container-aware overload: __KNIFX__ for the KNIFX container (Phase 35).
    // KNI's effect compiler always defines __KNIFX__=1; ShadowDusk targets KNI via the
    // KNIFX container, so it defines __KNIFX__ there (and only there) to match KNI.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public void For_KnifxContainer_AppendsKnifxMacro(PlatformTarget platform)
    {
        var macroSet = PlatformMacros.For(platform, EffectContainer.Knifx);

        macroSet.Macros.Select(m => m.Name).Should().Contain("__KNIFX__",
            because: "KNI's MojoEffectProcessor always defines __KNIFX__=1; ShadowDusk matches it for KNIFX");
        macroSet.Macros.Single(m => m.Name == "__KNIFX__").Value.Should().Be(1,
            because: "KNI defines it as __KNIFX__=1, so `#if __KNIFX__` (value check) also works");
        // The base target macros are preserved (and come first).
        macroSet.Macros.Select(m => m.Name).Should().StartWith(
            PlatformMacros.For(platform).Macros.Select(m => m.Name));
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.Vulkan)]
    public void For_MgfxContainer_IsIdenticalToBaseOverload_NoKnifxMacro(PlatformTarget platform)
    {
        var withContainer = PlatformMacros.For(platform, EffectContainer.Mgfx);
        var baseMacros    = PlatformMacros.For(platform);

        // The default MGFX container is target-agnostic — it must NOT define __KNIFX__
        // (that would make the universal output KNI-specific), and must equal the base set.
        withContainer.Macros.Select(m => m.Name).Should().NotContain("__KNIFX__");
        withContainer.Macros.Select(m => m.Name).Should().Equal(baseMacros.Macros.Select(m => m.Name));
    }

    // -------------------------------------------------------------------------
    // 2.3 — ToDxcFlags() produces interleaved -D NAME=VALUE strings
    // -------------------------------------------------------------------------

    [Fact]
    public void ToDxcFlags_DirectX_ProducesInterleavedFlags()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var flags = macroSet.ToDxcFlags();

        flags.Should().Equal("-D", "MGFX=1", "-D", "HLSL=1", "-D", "SM4=1");
    }

    [Fact]
    public void ToDxcFlags_OpenGL_ProducesInterleavedFlags()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.OpenGL);

        var flags = macroSet.ToDxcFlags();

        flags.Should().Equal("-D", "MGFX=1", "-D", "GLSL=1", "-D", "OPENGL=1");
    }

    [Fact]
    public void ToDxcFlags_AlwaysAlternatesDashDAndNameValuePairs()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var flags = macroSet.ToDxcFlags().ToArray();

        flags.Length.Should().Be(macroSet.Macros.Count * 2);

        for (int i = 0; i < flags.Length; i += 2)
        {
            flags[i].Should().Be("-D");
            flags[i + 1].Should().Contain("=");
        }
    }

    [Fact]
    public void ToDxcFlags_MacroValue_IsAppendedWithEqualsSign()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var flags = macroSet.ToDxcFlags();

        // Each name=value pair must be in the form NAME=VALUE (no spaces)
        var nameValuePairs = flags.Where((_, i) => i % 2 == 1).ToList();
        nameValuePairs.Should().OnlyContain(pair => pair.Contains('=') && !pair.Contains(' '));
    }

    // -------------------------------------------------------------------------
    // 2.4 — ToTextPrepend() content and structure
    // -------------------------------------------------------------------------

    [Fact]
    public void ToTextPrepend_ContainsGeneratedCommentHeader()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var prepend = macroSet.ToTextPrepend("foo.fx");

        prepend.Should().Contain("// ShadowDusk platform macros — DO NOT EDIT (generated)");
    }

    [Fact]
    public void ToTextPrepend_DirectX_ContainsAllDefineLines()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var prepend = macroSet.ToTextPrepend("foo.fx");

        prepend.Should().Contain("#define MGFX 1");
        prepend.Should().Contain("#define HLSL 1");
        prepend.Should().Contain("#define SM4 1");
    }

    [Fact]
    public void ToTextPrepend_OpenGL_ContainsAllDefineLines()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.OpenGL);

        var prepend = macroSet.ToTextPrepend("shader.fx");

        prepend.Should().Contain("#define MGFX 1");
        prepend.Should().Contain("#define GLSL 1");
        prepend.Should().Contain("#define OPENGL 1");
    }

    [Fact]
    public void ToTextPrepend_ContainsLineDirectivePointingToOriginalFile()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var prepend = macroSet.ToTextPrepend("foo.fx");

        prepend.Should().Contain("#line 1 \"foo.fx\"");
    }

    [Fact]
    public void ToTextPrepend_LineDirectiveAppearsAfterAllDefines()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var prepend = macroSet.ToTextPrepend("foo.fx");

        var lineDirectiveIndex = prepend.IndexOf("#line 1 \"foo.fx\"", StringComparison.Ordinal);
        var lastDefineIndex    = prepend.LastIndexOf("#define", StringComparison.Ordinal);

        lineDirectiveIndex.Should().BeGreaterThan(lastDefineIndex,
            because: "the #line reset directive must appear after all #define lines");
    }

    [Fact]
    public void ToTextPrepend_DefinesAppearInDeclarationOrder()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var prepend = macroSet.ToTextPrepend("foo.fx");

        var mgfxIndex = prepend.IndexOf("#define MGFX", StringComparison.Ordinal);
        var hlslIndex = prepend.IndexOf("#define HLSL", StringComparison.Ordinal);
        var sm4Index  = prepend.IndexOf("#define SM4",  StringComparison.Ordinal);

        mgfxIndex.Should().BeLessThan(hlslIndex);
        hlslIndex.Should().BeLessThan(sm4Index);
    }

    [Fact]
    public void ToTextPrepend_OriginalFilePathEmbeddedInLineDirective()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.OpenGL);

        var prepend = macroSet.ToTextPrepend("shaders/main.fx");

        prepend.Should().Contain("\"shaders/main.fx\"");
    }
}
