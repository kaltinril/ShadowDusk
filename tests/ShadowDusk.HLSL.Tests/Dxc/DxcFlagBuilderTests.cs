#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Dxc;

// DxcFlagBuilder is internal; InternalsVisibleTo is set in ShadowDusk.HLSL.csproj.
public sealed class DxcFlagBuilderTests
{
    private static IReadOnlyList<string> Build(
        PlatformTarget platform,
        ShaderStage stage,
        string entryPoint = "Main",
        IReadOnlyList<(string, string?)>? macros = null,
        DxcCompileOptions? options = null)
        => DxcFlagBuilder.Build(platform, stage, entryPoint, macros ?? [], options);

    private static string Joined(IReadOnlyList<string> flags) => string.Join(" ", flags);

    // ── OpenGL Vertex ────────────────────────────────────────────────────────

    [Fact] public void OpenGL_Vertex_HasSpirvFlag()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).Should().Contain("-spirv");

    [Fact] public void OpenGL_Vertex_HasProfile_vs5_0()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).Should().Contain("vs_5_0");

    [Fact] public void OpenGL_Vertex_HasDxLayout()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).Should().Contain("-fvk-use-dx-layout");

    [Fact] public void OpenGL_Vertex_HasDxPositionW()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).Should().Contain("-fvk-use-dx-position-w");

    [Fact] public void OpenGL_Vertex_DoesNotHaveInvertY()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).Should().NotContain("-fvk-invert-y");

    // ── OpenGL Pixel ─────────────────────────────────────────────────────────

    [Fact] public void OpenGL_Pixel_HasProfile_ps5_0()
        => Build(PlatformTarget.OpenGL, ShaderStage.Pixel).Should().Contain("ps_5_0");

    [Fact] public void OpenGL_Pixel_HasAutoBindingSpace1()
        => Joined(Build(PlatformTarget.OpenGL, ShaderStage.Pixel)).Should().Contain("-auto-binding-space");

    [Fact] public void OpenGL_Pixel_DoesNotHaveInvertY()
        => Build(PlatformTarget.OpenGL, ShaderStage.Pixel).Should().NotContain("-fvk-invert-y");

    // ── Vulkan Vertex ────────────────────────────────────────────────────────

    [Fact] public void Vulkan_Vertex_HasProfile_vs6_0()
        => Build(PlatformTarget.Vulkan, ShaderStage.Vertex).Should().Contain("vs_6_0");

    [Fact] public void Vulkan_Vertex_HasInvertY()
        => Build(PlatformTarget.Vulkan, ShaderStage.Vertex).Should().Contain("-fvk-invert-y");

    // Issue #145 (divergence S3): mgfxc ships SPIR-V compiled WITHOUT -fspv-reflect (it
    // compiles a second time to strip the Google VK extensions the flag forces into the
    // binary). ShadowDusk reads only core decorations + OpName debug names when reflecting,
    // so it never asks for the flag and ships the same clean module in ONE compile.
    [Fact] public void Vulkan_Vertex_HasNoFspvReflect()
        => Build(PlatformTarget.Vulkan, ShaderStage.Vertex).Should().NotContain("-fspv-reflect");

    [Fact] public void Vulkan_Vertex_HasSpirvFlag()
        => Build(PlatformTarget.Vulkan, ShaderStage.Vertex).Should().Contain("-spirv");

    [Fact] public void Vulkan_Vertex_HasTAndSShift()
        => Joined(Build(PlatformTarget.Vulkan, ShaderStage.Vertex))
            .Should().Contain("-fvk-t-shift 32 all").And.Contain("-fvk-s-shift 32 all");

    // ── Vulkan Pixel ─────────────────────────────────────────────────────────

    [Fact] public void Vulkan_Pixel_HasProfile_ps6_0()
        => Build(PlatformTarget.Vulkan, ShaderStage.Pixel).Should().Contain("ps_6_0");

    [Fact] public void Vulkan_Pixel_HasNoFspvReflect()
        => Build(PlatformTarget.Vulkan, ShaderStage.Pixel).Should().NotContain("-fspv-reflect");

    [Fact] public void Vulkan_Pixel_HasTAndSShift()
        => Joined(Build(PlatformTarget.Vulkan, ShaderStage.Pixel))
            .Should().Contain("-fvk-t-shift 32 all").And.Contain("-fvk-s-shift 32 all");

    // ── DirectX ──────────────────────────────────────────────────────────────

    // DXC minimum supported profile is SM6 — vs_6_0/ps_6_0 (not vs_5_0 DXBC)
    [Fact] public void DirectX_Vertex_HasProfile_vs6_0()
        => Build(PlatformTarget.DirectX, ShaderStage.Vertex).Should().Contain("vs_6_0");

    [Fact] public void DirectX_Vertex_DoesNotHaveSpirvFlag()
        => Build(PlatformTarget.DirectX, ShaderStage.Vertex).Should().NotContain("-spirv");

    [Fact] public void DirectX_Pixel_HasProfile_ps6_0()
        => Build(PlatformTarget.DirectX, ShaderStage.Pixel).Should().Contain("ps_6_0");

    [Fact] public void DirectX_Pixel_DoesNotHaveSpirvFlag()
        => Build(PlatformTarget.DirectX, ShaderStage.Pixel).Should().NotContain("-spirv");

    // ── Entry point ───────────────────────────────────────────────────────────

    [Fact]
    public void EntryPoint_AppearsAfterDashE()
    {
        var flags = Build(PlatformTarget.OpenGL, ShaderStage.Vertex, entryPoint: "VSMain");
        int idx = flags.ToList().IndexOf("-E");
        idx.Should().BeGreaterThanOrEqualTo(0, "'-E' must be present");
        flags[idx + 1].Should().Be("VSMain");
    }

    [Fact]
    public void EntryPoint_PrecedesProfileArgument()
    {
        // Phase 4 checklist: "-E <entryPoint> appears before the profile argument".
        var flags = Build(PlatformTarget.OpenGL, ShaderStage.Vertex, entryPoint: "VSMain").ToList();
        int entryIdx   = flags.IndexOf("-E");
        int profileIdx = flags.IndexOf("-T");
        entryIdx.Should().BeGreaterThanOrEqualTo(0, "'-E' must be present");
        profileIdx.Should().BeGreaterThan(entryIdx, "'-T <profile>' must follow '-E <entryPoint>'");
    }

    // ── Invariant flags ───────────────────────────────────────────────────────

    [Fact] public void ZprPresentForOpenGL()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).Should().Contain("-Zpr");

    // Issue #145 (bug 1): -Zpr made DXC pack matrices ROW-major, but MonoGame's runtime
    // uploads a Matrix parameter for HLSL's COLUMN-major default (EffectParameter
    // .SetValue(Matrix) transposes on assignment), so every matrix reached a Vulkan shader
    // transposed and VS-driven effects rendered nothing. mgfxc's Vulkan command line has no
    // -Zpr; neither may ours. OpenGL keeps it (MonoGameGlslRewriter.BuildUploadedMat4
    // compensates, the issue-#70 fix) and DirectX never reaches DxcFlagBuilder.
    [Theory]
    [InlineData(ShaderStage.Vertex)]
    [InlineData(ShaderStage.Pixel)]
    public void ZprAbsentForVulkan(ShaderStage stage)
        => Build(PlatformTarget.Vulkan, stage).Should().NotContain("-Zpr");

    [Fact] public void WxPresentByDefault()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).Should().Contain("-WX");

    [Fact] public void WxAbsentWhenAllowWarnings()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex, options: new DxcCompileOptions { AllowWarnings = true })
            .Should().NotContain("-WX");

    [Fact] public void DebugFlags_AbsentByDefault()
    {
        var flags = Build(PlatformTarget.OpenGL, ShaderStage.Vertex);
        flags.Should().NotContain("-Zi");
        flags.Should().NotContain("-Qembed_debug");
    }

    [Fact] public void DebugFlags_PresentWhenEmbedDebugInfo()
    {
        var flags = Build(PlatformTarget.OpenGL, ShaderStage.Vertex, options: new DxcCompileOptions { EmbedDebugInfo = true });
        flags.Should().Contain("-Zi");
        flags.Should().Contain("-Qembed_debug");
    }

    // ── Macros ────────────────────────────────────────────────────────────────

    [Fact]
    public void MacroWithValue_FormatsAsDashDNameEqualsValue()
    {
        var flags = Build(PlatformTarget.OpenGL, ShaderStage.Vertex,
            macros: [("FOO", "1")]);
        flags.Should().Contain("-DFOO=1");
    }

    [Fact]
    public void MacroWithNullValue_FormatsAsDashDName()
    {
        var flags = Build(PlatformTarget.OpenGL, ShaderStage.Vertex,
            macros: [("BAR", null)]);
        flags.Should().Contain("-DBAR");
        flags.Should().NotContain("-DBAR=");
    }
}
