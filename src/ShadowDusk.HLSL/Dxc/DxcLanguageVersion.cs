#nullable enable

using ShadowDusk.Core;
using ShadowDusk.HLSL.Preprocessing;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Which HLSL language version (DXC's <c>-HV</c>) a compile runs at (Phase 70, issue #287).
/// </summary>
/// <remarks>
/// <para>
/// DXC's default moved from 2018 to 2021 in v1.7.2308, so the version is always stated. It is
/// chosen per target, by what that target's reference compiler does:
/// </para>
/// <list type="bullet">
/// <item><b>DirectX 12 and Vulkan run 2021.</b> <c>mgfxc</c> calls DXC there with no <c>-HV</c>,
///   so 2021 is the language of the reference output. It changes code, not only acceptance:
///   <c>&amp;&amp;</c>, <c>||</c> and <c>?:</c> short-circuit, and at 2018 the Apos.Shapes pixel
///   shader compiled to different control flow than <c>mgfxc</c>'s (measured on v1.9.2607).</item>
/// <item><b>Every other DXC compile runs 2018</b> (OpenGL, and the DirectX 11 reflection
///   companion). Their reference is <c>fxc</c>, which evaluates both sides.</item>
/// </list>
/// <para>
/// 2021 rejects a vector condition in <c>?:</c> and vector <c>&amp;&amp;</c> / <c>||</c>, which
/// FX shaders use and which ShadowDusk has always compiled for these targets. A source 2021
/// refuses is therefore compiled again at 2018 (<see cref="MayRetryAsLegacy"/>), so moving the
/// pin stops no shader from compiling.
/// </para>
/// </remarks>
internal static class DxcLanguageVersion
{
    /// <summary>DXC's language-version switch.</summary>
    internal const string Flag = "-HV";

    /// <summary>The language of the <c>fxc</c>-referenced targets, and the fallback.</summary>
    internal const string Legacy = "2018";

    /// <summary>The language <c>mgfxc</c>'s DXC compiles DirectX 12 and Vulkan in.</summary>
    internal const string Modern = "2021";

    /// <summary>The language version a compile for <paramref name="platform"/> runs at first.</summary>
    internal static string For(PlatformTarget platform) =>
        platform is PlatformTarget.DirectX12 or PlatformTarget.Vulkan ? Modern : Legacy;

    /// <summary>
    /// True when the compile of <paramref name="request"/>, which failed at <see cref="Modern"/>
    /// with <paramref name="error"/>, should be compiled again at <see cref="Legacy"/>.
    /// </summary>
    /// <remarks>
    /// Only DXC's own diagnostics qualify: an <c>SD</c> code is a ShadowDusk refusal, which a
    /// language version cannot change. On Vulkan the two versions must also give the texture/sampler
    /// pairing the same view of the source, because it has already read the code as DXC compiles
    /// it at 2021 (<c>DxcSpirvPredefinedMacros</c>): a retry that took another <c>#if</c> branch
    /// would compile code the pairing never saw, so that source keeps its 2021 error.
    /// </remarks>
    internal static bool MayRetryAsLegacy(DxcCompileRequest request, ShaderError error) =>
        For(request.Platform) == Modern
        && !error.Code.StartsWith("SD", StringComparison.Ordinal)
        && (request.Platform != PlatformTarget.Vulkan
            || !ChangesPairingView(request.HlslSource, request.SourceFileName, request.Stage));

    /// <summary>
    /// True when <c>__HLSL_VERSION</c> at 2018 and at 2021 gives a different managed view of
    /// <paramref name="source"/>. A source with no usable view is paired over the whole file,
    /// which no language version changes.
    /// </summary>
    internal static bool ChangesPairingView(string source, string sourceFileName, ShaderStage stage)
    {
        var modern = FxMacroPreprocessor.SkippedLines(
            source, sourceFileName, DxcSpirvPredefinedMacros.Vulkan(stage, Modern));
        if (modern.IsFailure || modern.Value.PredefinedMacroUse is not null)
            return false;

        var legacy = FxMacroPreprocessor.SkippedLines(
            source, sourceFileName, DxcSpirvPredefinedMacros.Vulkan(stage, Legacy));
        return legacy.IsFailure
            || legacy.Value.PredefinedMacroUse is not null
            || !modern.Value.Lines.SetEquals(legacy.Value.Lines)
            || !string.Equals(modern.Value.View, legacy.Value.View, StringComparison.Ordinal);
    }
}
