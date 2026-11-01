#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Preprocessing;

/// <summary>
/// The macros the pinned DXC predefines for a Vulkan (<c>-spirv</c>) compile whose value does not
/// depend on the host, as measured, so the Vulkan texture/sampler pairing's view of which lines DXC
/// compiles (<see cref="FxMacroPreprocessor.SkippedLines"/>) can evaluate a conditional on them
/// instead of giving up on the whole view (issue #442).
/// </summary>
/// <remarks>
/// <para><b>Measured</b> 2026-10-31 with <c>-P</c> and ShadowDusk's exact Vulkan argument sets
/// (<c>vs_6_0</c> / <c>ps_6_0</c>, <c>-spirv</c>, the <c>-fvk-*</c> flags) on Vortice.Dxc 3.3.4's
/// win-x64 DXC (<c>1.7.2212.40 (e043f4a12)</c>) and its linux-x64 DXC (which identifies as
/// <c>dxc(private) 1.7.0.3759</c>): every value below is the same on both.
/// <c>DxcSpirvPredefinedMacrosTests</c> re-measures them against the loaded DXC on every host the
/// integration suite runs on, so a DXC change (issue #287) that moves one fails loudly.</para>
/// <para><b>Deliberately left out:</b> <c>__DXC_VERSION_MAJOR/MINOR/RELEASE/COMMITS</c>. They are
/// the DXC build's own version stamp and differ by host for the SAME pinned DXC: 1.7.2212.40 on
/// Windows, 1.7.0.3759 on Linux (measured the same day). A conditional on one keeps the view
/// untrusted, so the pairing falls back to the whole file (the released behavior, with
/// <c>SD0235</c> where it aliases), never a guess. <c>__LINE__</c>, <c>__COUNTER__</c>,
/// <c>__TIME__</c>, the <c>__FLT_*</c>/<c>__DBL_*</c> families and the <c>__has_*</c> builtins
/// likewise stay untrusted.</para>
/// </remarks>
internal static class DxcSpirvPredefinedMacros
{
    /// <summary>The DXC these values were measured on (<c>DxcNativeIdentity.WindowsCompilerVersion</c>).</summary>
    internal const string MeasuredOnDxc = "1.7.2212.40 (e043f4a12)";

    // DXIL::ShaderKind numbering, as DXC prints __SHADER_STAGE_* (measured).
    private static readonly (string Name, string Value)[] StageKinds =
    [
        ("__SHADER_STAGE_PIXEL", "0"),
        ("__SHADER_STAGE_VERTEX", "1"),
        ("__SHADER_STAGE_GEOMETRY", "2"),
        ("__SHADER_STAGE_HULL", "3"),
        ("__SHADER_STAGE_DOMAIN", "4"),
        ("__SHADER_STAGE_COMPUTE", "5"),
        ("__SHADER_STAGE_LIBRARY", "6"),
        ("__SHADER_STAGE_MESH", "13"),
        ("__SHADER_STAGE_AMPLIFICATION", "14"),
    ];

    /// <summary>
    /// The predefined macros of the Vulkan compile of a <paramref name="stage"/> shader (ShadowDusk
    /// compiles every Vulkan shader at <c>vs_6_0</c> / <c>ps_6_0</c>, see <c>DxcFlagBuilder</c>).
    /// Only <c>__SHADER_TARGET_STAGE</c> depends on the stage.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Value)> Vulkan(ShaderStage stage)
    {
        var macros = new List<(string Name, string Value)>
        {
            ("__hlsl_dx_compiler", "1"),
            ("__HLSL_VERSION", "2018"),
            ("__SHADER_TARGET_MAJOR", "6"),
            ("__SHADER_TARGET_MINOR", "0"),
            ("__SHADER_TARGET_STAGE", stage == ShaderStage.Vertex ? "1" : "0"),
            ("__spirv__", "1"),
        };
        macros.AddRange(StageKinds);
        return macros;
    }
}
