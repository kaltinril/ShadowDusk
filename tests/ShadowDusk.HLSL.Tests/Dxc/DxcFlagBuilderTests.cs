#nullable enable

using Shouldly;
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
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).ShouldContain("-spirv");

    [Fact] public void OpenGL_Vertex_HasProfile_vs5_0()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).ShouldContain("vs_5_0");

    [Fact] public void OpenGL_Vertex_HasDxLayout()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).ShouldContain("-fvk-use-dx-layout");

    [Fact] public void OpenGL_Vertex_HasDxPositionW()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).ShouldContain("-fvk-use-dx-position-w");

    [Fact] public void OpenGL_Vertex_DoesNotHaveInvertY()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).ShouldNotContain("-fvk-invert-y");

    // ── OpenGL Pixel ─────────────────────────────────────────────────────────

    [Fact] public void OpenGL_Pixel_HasProfile_ps5_0()
        => Build(PlatformTarget.OpenGL, ShaderStage.Pixel).ShouldContain("ps_5_0");

    [Fact] public void OpenGL_Pixel_HasAutoBindingSpace1()
        => Joined(Build(PlatformTarget.OpenGL, ShaderStage.Pixel)).ShouldContain("-auto-binding-space");

    [Fact] public void OpenGL_Pixel_DoesNotHaveInvertY()
        => Build(PlatformTarget.OpenGL, ShaderStage.Pixel).ShouldNotContain("-fvk-invert-y");

    // ── Vulkan Vertex ────────────────────────────────────────────────────────

    [Fact] public void Vulkan_Vertex_HasProfile_vs6_0()
        => Build(PlatformTarget.Vulkan, ShaderStage.Vertex).ShouldContain("vs_6_0");

    [Fact] public void Vulkan_Vertex_HasInvertY()
        => Build(PlatformTarget.Vulkan, ShaderStage.Vertex).ShouldContain("-fvk-invert-y");

    // Issue #145 (divergence S3): mgfxc ships SPIR-V compiled WITHOUT -fspv-reflect (it
    // compiles a second time to strip the Google VK extensions the flag forces into the
    // binary). ShadowDusk reads only core decorations + OpName debug names when reflecting,
    // so it never asks for the flag and ships the same clean module in ONE compile.
    [Fact] public void Vulkan_Vertex_HasNoFspvReflect()
        => Build(PlatformTarget.Vulkan, ShaderStage.Vertex).ShouldNotContain("-fspv-reflect");

    [Fact] public void Vulkan_Vertex_HasSpirvFlag()
        => Build(PlatformTarget.Vulkan, ShaderStage.Vertex).ShouldContain("-spirv");

    [Fact] public void Vulkan_Vertex_HasTAndSShift()
    {
        string joined = Joined(Build(PlatformTarget.Vulkan, ShaderStage.Vertex));
        joined.ShouldContain("-fvk-t-shift 32 all", Case.Sensitive);
        joined.ShouldContain("-fvk-s-shift 32 all", Case.Sensitive);
    }

    // Issue #229: ShadowDusk never asks DXC for a Vulkan 1.1 SPIR-V target env. MonoGame's
    // DesktopVK creates a Vulkan 1.0 instance, so a 1.1 module (what wave/quad intrinsics need)
    // is out of spec there; those shaders are rejected with SD0218 instead.
    [Theory]
    [InlineData(ShaderStage.Vertex)]
    [InlineData(ShaderStage.Pixel)]
    public void Vulkan_NeverRequestsATargetEnv(ShaderStage stage)
        => Joined(Build(PlatformTarget.Vulkan, stage)).ShouldNotContain("target-env", Case.Sensitive);

    // ── Vulkan Pixel ─────────────────────────────────────────────────────────

    [Fact] public void Vulkan_Pixel_HasProfile_ps6_0()
        => Build(PlatformTarget.Vulkan, ShaderStage.Pixel).ShouldContain("ps_6_0");

    [Fact] public void Vulkan_Pixel_HasNoFspvReflect()
        => Build(PlatformTarget.Vulkan, ShaderStage.Pixel).ShouldNotContain("-fspv-reflect");

    [Fact] public void Vulkan_Pixel_HasTAndSShift()
    {
        string joined = Joined(Build(PlatformTarget.Vulkan, ShaderStage.Pixel));
        joined.ShouldContain("-fvk-t-shift 32 all", Case.Sensitive);
        joined.ShouldContain("-fvk-s-shift 32 all", Case.Sensitive);
    }

    // ── DirectX ──────────────────────────────────────────────────────────────

    // DXC minimum supported profile is SM6 — vs_6_0/ps_6_0 (not vs_5_0 DXBC)
    [Fact] public void DirectX_Vertex_HasProfile_vs6_0()
        => Build(PlatformTarget.DirectX, ShaderStage.Vertex).ShouldContain("vs_6_0");

    [Fact] public void DirectX_Vertex_DoesNotHaveSpirvFlag()
        => Build(PlatformTarget.DirectX, ShaderStage.Vertex).ShouldNotContain("-spirv");

    [Fact] public void DirectX_Pixel_HasProfile_ps6_0()
        => Build(PlatformTarget.DirectX, ShaderStage.Pixel).ShouldContain("ps_6_0");

    [Fact] public void DirectX_Pixel_DoesNotHaveSpirvFlag()
        => Build(PlatformTarget.DirectX, ShaderStage.Pixel).ShouldNotContain("-spirv");

    // ── Entry point ───────────────────────────────────────────────────────────

    [Fact]
    public void EntryPoint_AppearsAfterDashE()
    {
        var flags = Build(PlatformTarget.OpenGL, ShaderStage.Vertex, entryPoint: "VSMain");
        int idx = flags.ToList().IndexOf("-E");
        idx.ShouldBeGreaterThanOrEqualTo(0, customMessage: "'-E' must be present");
        flags[idx + 1].ShouldBe("VSMain");
    }

    [Fact]
    public void EntryPoint_PrecedesProfileArgument()
    {
        // Phase 4 checklist: "-E <entryPoint> appears before the profile argument".
        var flags = Build(PlatformTarget.OpenGL, ShaderStage.Vertex, entryPoint: "VSMain").ToList();
        int entryIdx   = flags.IndexOf("-E");
        int profileIdx = flags.IndexOf("-T");
        entryIdx.ShouldBeGreaterThanOrEqualTo(0, customMessage: "'-E' must be present");
        profileIdx.ShouldBeGreaterThan(entryIdx, customMessage: "'-T <profile>' must follow '-E <entryPoint>'");
    }

    // ── Invariant flags ───────────────────────────────────────────────────────

    [Fact] public void ZprPresentForOpenGL()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).ShouldContain("-Zpr");

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
        => Build(PlatformTarget.Vulkan, stage).ShouldNotContain("-Zpr");

    [Fact] public void WxPresentByDefault()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex).ShouldContain("-WX");

    [Fact] public void WxAbsentWhenAllowWarnings()
        => Build(PlatformTarget.OpenGL, ShaderStage.Vertex, options: new DxcCompileOptions { AllowWarnings = true })
            .ShouldNotContain("-WX");

    [Fact] public void DebugFlags_AbsentByDefault()
    {
        var flags = Build(PlatformTarget.OpenGL, ShaderStage.Vertex);
        flags.ShouldNotContain("-Zi");
        flags.ShouldNotContain("-Qembed_debug");
    }

    [Fact] public void DebugFlags_PresentWhenEmbedDebugInfo()
    {
        var flags = Build(PlatformTarget.OpenGL, ShaderStage.Vertex, options: new DxcCompileOptions { EmbedDebugInfo = true });
        flags.ShouldContain("-Zi");
        flags.ShouldContain("-Qembed_debug");
    }

    // Issue #343: a debug SPIR-V compile names its input something no host can open, so DXC's
    // OpSource read of the main file fails everywhere and the in-memory source is embedded.
    [Theory]
    [InlineData(PlatformTarget.OpenGL, ShaderStage.Vertex)]
    [InlineData(PlatformTarget.OpenGL, ShaderStage.Pixel)]
    [InlineData(PlatformTarget.Vulkan, ShaderStage.Vertex)]
    [InlineData(PlatformTarget.Vulkan, ShaderStage.Pixel)]
    [InlineData(PlatformTarget.Metal, ShaderStage.Pixel)]
    public void DebugSpirv_NamesTheUnopenableInput(PlatformTarget platform, ShaderStage stage)
    {
        var flags = Build(platform, stage, options: new DxcCompileOptions { EmbedDebugInfo = true });
        flags.Count(f => f == DxcDebugSpirvSource.InputName).ShouldBe(1);
        flags.Count(f => !f.StartsWith('-') && f.Contains("hlsl", StringComparison.Ordinal)).ShouldBe(1,
            "the input name must be the only positional argument DXC sees");
    }

    // Release compiles and DXIL keep DXC's default input name, so their arguments (and bytes)
    // are exactly what they were before issue #343.
    [Theory]
    [InlineData(PlatformTarget.OpenGL, false)]
    [InlineData(PlatformTarget.Vulkan, false)]
    [InlineData(PlatformTarget.DirectX, false)]
    [InlineData(PlatformTarget.DirectX12, false)]
    [InlineData(PlatformTarget.DirectX, true)]
    [InlineData(PlatformTarget.DirectX12, true)]
    public void ReleaseAndDxil_PassNoInputName(PlatformTarget platform, bool debug)
    {
        foreach (ShaderStage stage in new[] { ShaderStage.Vertex, ShaderStage.Pixel })
        {
            var flags = Build(platform, stage, options: new DxcCompileOptions { EmbedDebugInfo = debug });
            flags.ShouldNotContain(DxcDebugSpirvSource.InputName);
        }
    }

    [Fact] public void DebugSpirvInputName_CannotBeOpenedOnAnyHost()
    {
        // Unix: under /dev/null, which is not a directory. Windows: '<' and '>' are not legal in
        // a Win32 file name. DXC's option parser takes a '/'-led argument that matches no option
        // as an input path, which the integration probe confirms by compiling with it.
        DxcDebugSpirvSource.InputName.ShouldStartWith("/dev/null/", Case.Sensitive);
        DxcDebugSpirvSource.InputName.IndexOfAny(['<', '>']).ShouldBeGreaterThan(0);
        DxcDebugSpirvSource.InputName.ShouldNotContain(":", Case.Sensitive,
            "a colon would be an NTFS stream separator and would confuse the file:line:col diagnostic parse");
    }

    // Issue #185: the OpenGL path's reflection-only DirectX-target companion compile passes
    // SkipValidation so a hosted CI runner's version-skewed dxil.dll (ahead of the pinned one
    // on the native search path) can't reject an otherwise-correct DXIL module with a "DXIL
    // container mismatch" validation error. Never set for shipped bytecode.
    [Fact] public void VdAbsentByDefault()
        => Build(PlatformTarget.DirectX, ShaderStage.Pixel).ShouldNotContain("-Vd");

    [Fact] public void VdPresentWhenSkipValidation()
        => Build(PlatformTarget.DirectX, ShaderStage.Pixel, options: new DxcCompileOptions { SkipValidation = true })
            .ShouldContain("-Vd");

    // ── Macros ────────────────────────────────────────────────────────────────

    [Fact]
    public void MacroWithValue_FormatsAsDashDNameEqualsValue()
    {
        var flags = Build(PlatformTarget.OpenGL, ShaderStage.Vertex,
            macros: [("FOO", "1")]);
        flags.ShouldContain("-DFOO=1");
    }

    [Fact]
    public void MacroWithNullValue_FormatsAsDashDName()
    {
        var flags = Build(PlatformTarget.OpenGL, ShaderStage.Vertex,
            macros: [("BAR", null)]);
        flags.ShouldContain("-DBAR");
        flags.ShouldNotContain("-DBAR=");
    }
}
