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
/// refuses is therefore compiled again at 2018 (<see cref="MayRetryAsLegacy"/>) and carries the
/// warning <see cref="LegacyFallbackCode"/>, because <c>mgfxc</c> would not compile it here. The
/// one exception is a Vulkan source whose preprocessed view depends on <c>__HLSL_VERSION</c>:
/// it keeps its 2021 error (accepted by the owner, <c>project_decisions.md</c>).
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

    /// <summary>The warning on a shader that only compiled at <see cref="Legacy"/>.</summary>
    internal const string LegacyFallbackCode = "SD0242";

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
    /// The outcome of the retry at <see cref="Legacy"/> after <paramref name="modern"/> refused the
    /// source: the blob with a <see cref="LegacyFallbackCode"/> warning, or, when both versions
    /// fail, the diagnostic of the attempt that got further through the source. That is the real
    /// error whichever language the source is written in: a 2021-only construct stops a 2018
    /// compile early, a vector condition stops a 2021 compile early. Both texts stay in
    /// <see cref="ShaderError.RawDiagnostics"/>, verbatim.
    /// </summary>
    internal static Result<PlatformBlob, ShaderError> AfterLegacyRetry(
        ShaderError modern, Result<PlatformBlob, ShaderError> legacy)
    {
        if (legacy.IsFailure)
            return Result<PlatformBlob, ShaderError>.Fail(ReportBoth(modern, legacy.Error));

        var notice = new ShaderError(
            modern.File,
            modern.Line,
            modern.Column,
            LegacyFallbackCode,
            "this shader compiled as HLSL 2018 because HLSL 2021, the language mgfxc's DXC uses for this " +
            $"target, refused it, so mgfxc would not compile it here. HLSL 2021 said: {modern.Message}",
            ShaderErrorSeverity.Warning);
        return Result<PlatformBlob, ShaderError>.Ok(
            new PlatformBlob(legacy.Value.Kind, legacy.Value.Bytes) { Warnings = [notice, .. legacy.Value.Warnings] });
    }

    private static ShaderError ReportBoth(ShaderError modern, ShaderError legacy)
    {
        bool legacyGotFurther = string.Equals(modern.File, legacy.File, StringComparison.Ordinal)
            && (legacy.Line, legacy.Column).CompareTo((modern.Line, modern.Column)) > 0;
        ShaderError primary = legacyGotFurther ? legacy : modern;
        return primary with
        {
            RawDiagnostics =
                $"HLSL 2021:\n{modern.RawDiagnostics ?? modern.Message}\n" +
                $"HLSL 2018:\n{legacy.RawDiagnostics ?? legacy.Message}",
        };
    }

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
