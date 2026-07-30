#nullable enable

using Shouldly;
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
            .ShouldBe(new[] {"MGFX", "HLSL", "SM4"});
    }

    [Fact]
    public void For_OpenGL_ReturnsExactlyMgfxGlslOpengl()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.OpenGL);

        macroSet.Macros.Select(m => m.Name)
            .ShouldBe(new[] {"MGFX", "GLSL", "OPENGL"});
    }

    [Fact]
    public void For_Vulkan_ReturnsExactlyMgfxHlslVulkanSm6()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.Vulkan);

        macroSet.Macros.Select(m => m.Name)
            .ShouldBe(new[] {"MGFX", "HLSL", "VULKAN", "SM6"});
    }

    [Theory]
    [InlineData(PlatformTarget.DirectX, 3)]
    [InlineData(PlatformTarget.OpenGL, 3)]
    [InlineData(PlatformTarget.Vulkan, 4)]
    public void For_KnownPlatform_HasExpectedMacroCount(PlatformTarget platform, int expectedCount)
    {
        var macroSet = PlatformMacros.For(platform);

        macroSet.Macros.Count().ShouldBe(expectedCount);
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

        macroSet.Macros.Select(m => m.Name).ShouldContain("__KNIFX__", "KNI's MojoEffectProcessor always defines __KNIFX__=1; ShadowDusk matches it for KNIFX");
        macroSet.Macros.Single(m => m.Name == "__KNIFX__").Value.ShouldBe(1, customMessage: "KNI defines it as __KNIFX__=1, so `#if __KNIFX__` (value check) also works");
        // The base target macros are preserved (and come first).
        // Shouldly has no collection-prefix assertion, so the prefix is taken explicitly.
        string[] baseNames = PlatformMacros.For(platform).Macros.Select(m => m.Name).ToArray();
        macroSet.Macros.Select(m => m.Name).Take(baseNames.Length).ShouldBe(baseNames);
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
        withContainer.Macros.Select(m => m.Name).ShouldNotContain("__KNIFX__");
        withContainer.Macros.Select(m => m.Name).ShouldBe(baseMacros.Macros.Select(m => m.Name));
    }

    // -------------------------------------------------------------------------
    // 2.3 — ToDxcFlags() produces interleaved -D NAME=VALUE strings
    // -------------------------------------------------------------------------

    [Fact]
    public void ToDxcFlags_DirectX_ProducesInterleavedFlags()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var flags = macroSet.ToDxcFlags();

        flags.ShouldBe(new[] {"-D", "MGFX=1", "-D", "HLSL=1", "-D", "SM4=1"});
    }

    [Fact]
    public void ToDxcFlags_OpenGL_ProducesInterleavedFlags()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.OpenGL);

        var flags = macroSet.ToDxcFlags();

        flags.ShouldBe(new[] {"-D", "MGFX=1", "-D", "GLSL=1", "-D", "OPENGL=1"});
    }

    [Fact]
    public void ToDxcFlags_AlwaysAlternatesDashDAndNameValuePairs()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var flags = macroSet.ToDxcFlags().ToArray();

        flags.Length.ShouldBe(macroSet.Macros.Count * 2);

        for (int i = 0; i < flags.Length; i += 2)
        {
            flags[i].ShouldBe("-D");
            flags[i + 1].ShouldContain("=", Case.Sensitive);
        }
    }

    [Fact]
    public void ToDxcFlags_MacroValue_IsAppendedWithEqualsSign()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var flags = macroSet.ToDxcFlags();

        // Each name=value pair must be in the form NAME=VALUE (no spaces)
        var nameValuePairs = flags.Where((_, i) => i % 2 == 1).ToList();
        nameValuePairs.ShouldAllBe(pair => pair.Contains('=') && !pair.Contains(' '));
    }

    // -------------------------------------------------------------------------
    // 2.4 — ToTextPrepend() content and structure
    // -------------------------------------------------------------------------

    [Fact]
    public void ToTextPrepend_ContainsGeneratedCommentHeader()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var prepend = macroSet.ToTextPrepend("foo.fx");

        prepend.ShouldContain("// ShadowDusk platform macros — DO NOT EDIT (generated)", Case.Sensitive);
    }

    [Fact]
    public void ToTextPrepend_DirectX_ContainsAllDefineLines()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var prepend = macroSet.ToTextPrepend("foo.fx");

        prepend.ShouldContain("#define MGFX 1", Case.Sensitive);
        prepend.ShouldContain("#define HLSL 1", Case.Sensitive);
        prepend.ShouldContain("#define SM4 1", Case.Sensitive);
    }

    [Fact]
    public void ToTextPrepend_OpenGL_ContainsAllDefineLines()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.OpenGL);

        var prepend = macroSet.ToTextPrepend("shader.fx");

        prepend.ShouldContain("#define MGFX 1", Case.Sensitive);
        prepend.ShouldContain("#define GLSL 1", Case.Sensitive);
        prepend.ShouldContain("#define OPENGL 1", Case.Sensitive);
    }

    [Fact]
    public void ToTextPrepend_ContainsLineDirectivePointingToOriginalFile()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var prepend = macroSet.ToTextPrepend("foo.fx");

        prepend.ShouldContain("#line 1 \"foo.fx\"", Case.Sensitive);
    }

    [Fact]
    public void ToTextPrepend_LineDirectiveAppearsAfterAllDefines()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var prepend = macroSet.ToTextPrepend("foo.fx");

        var lineDirectiveIndex = prepend.IndexOf("#line 1 \"foo.fx\"", StringComparison.Ordinal);
        var lastDefineIndex    = prepend.LastIndexOf("#define", StringComparison.Ordinal);

        lineDirectiveIndex.ShouldBeGreaterThan(lastDefineIndex, customMessage: "the #line reset directive must appear after all #define lines");
    }

    [Fact]
    public void ToTextPrepend_DefinesAppearInDeclarationOrder()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.DirectX);

        var prepend = macroSet.ToTextPrepend("foo.fx");

        var mgfxIndex = prepend.IndexOf("#define MGFX", StringComparison.Ordinal);
        var hlslIndex = prepend.IndexOf("#define HLSL", StringComparison.Ordinal);
        var sm4Index  = prepend.IndexOf("#define SM4",  StringComparison.Ordinal);

        mgfxIndex.ShouldBeLessThan(hlslIndex);
        hlslIndex.ShouldBeLessThan(sm4Index);
    }

    [Fact]
    public void ToTextPrepend_OriginalFilePathEmbeddedInLineDirective()
    {
        var macroSet = PlatformMacros.For(PlatformTarget.OpenGL);

        var prepend = macroSet.ToTextPrepend("shaders/main.fx");

        prepend.ShouldContain("\"shaders/main.fx\"", Case.Sensitive);
    }
}
