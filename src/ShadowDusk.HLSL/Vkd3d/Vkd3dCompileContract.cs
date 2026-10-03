#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.HLSL.Vkd3d;

/// <summary>
/// The PURE request→ABI mapping and error mapping shared by every vkd3d-shader
/// backend host — the desktop P/Invoke backend (<see cref="Vkd3dShaderCompiler"/>)
/// and the browser/WASM backend (<c>ShadowDusk.Wasm.WasmVkd3dShaderCompiler</c>, via
/// <c>InternalsVisibleTo</c>). Centralizing it here is what makes the two hosts
/// semantically one backend (Phase 4.1): same source text handed to vkd3d (issue #319),
/// same profile defaults, same SM ≤ 3 → D3D_BYTECODE routing, same vkd3d compile
/// options (issue #295), same diagnostic fidelity on failure AND on a success that
/// carries non-fatal diagnostics (issue #335) — so the only difference between hosts
/// is HOW the native vkd3d call is made, never WHAT is asked of it or what comes back.
///
/// <para>No I/O, no interop, no process — unit-testable per the conventions
/// (<c>Vkd3dCompileContractTests</c>).</para>
/// </summary>
internal static class Vkd3dCompileContract
{
    /// <summary>
    /// VKD3D_SHADER_TARGET_D3D_BYTECODE — the bare legacy D3D9 token stream
    /// (SM1–3, the FNA fx_2_0 path). Value pinned by the vkd3d 2.1 ABI and the
    /// Phase 4.1 WASM wrapper contract (<c>sdw_vkd3d_compile</c> target_type = 4);
    /// must equal <see cref="Vkd3dTargetType.D3dBytecode"/>.
    /// </summary>
    public const int TargetTypeD3dBytecode = 4;

    /// <summary>
    /// VKD3D_SHADER_TARGET_DXBC_TPF — the DXBC container MonoGame's DX11 runtime
    /// loads (SM4/5). Value pinned by the vkd3d 2.1 ABI and the Phase 4.1 WASM
    /// wrapper contract (<c>sdw_vkd3d_compile</c> target_type = 5); must equal
    /// <see cref="Vkd3dTargetType.DxbcTpf"/>.
    /// </summary>
    public const int TargetTypeDxbcTpf = 5;

    // Matches a whole #line directive line, without its newline, so blanking it leaves an
    // empty line and the overall line numbering is preserved.
    private static readonly Regex LineDirectivePattern =
        new(@"(?m)^[ \t]*#[ \t]*line\b[^\n]*", RegexOptions.Compiled);

    /// <summary>
    /// The text EVERY host hands <c>vkd3d_shader_compile</c> for a request: the preprocessed
    /// HLSL with every <c>#line</c> directive line blanked. This is the only place the
    /// source is prepared for vkd3d; the desktop backend marshals exactly this string and
    /// the browser backend sends exactly this string through its shim, so the two hosts
    /// cannot compile different text (issue #319: the browser used to hand vkd3d the
    /// directives, and vkd3d printed one <c>fixme</c> line per directive to the console).
    ///
    /// <para><b>Why blank the directives.</b> vkd3d-shader's HLSL preprocessor does not
    /// honour <c>#line</c>; it ignores each one and (at log level warning or above) prints
    /// <c>vkd3d:NNNN:fixme:vkd3d:preproc_yyparse #line directive.</c> to the process's
    /// stderr (the browser's console), once per directive. An include-heavy effect (the
    /// MonoGame stock effects pull in Macros.fxh/Structures.fxh/Common.fxh/Lighting.fxh)
    /// carries hundreds of them from the include flattener, so a SUCCESSFUL compile would
    /// spew hundreds of lines and break the mgfxc silent-success contract. The
    /// <c>VKD3D_DEBUG=none</c> default the desktop loader sets does not reliably reach the
    /// native getenv, and the WASM module has no environment at all.</para>
    ///
    /// <para><b>Why blank, not delete.</b> BLANKING keeps the prepared text and the
    /// request's <c>HlslSource</c> line-for-line aligned, which is what lets
    /// <see cref="Vkd3dSourceLocator"/> map vkd3d's coordinates back through the directives
    /// (issue #202). Measured on the 91-compile DX + FNA corpus (issue #319): the bytes
    /// vkd3d emits and the positions of its diagnostics are identical with and without the
    /// directives, so this changes only what reaches stderr. The DXC and d3dcompiler_47
    /// paths keep their directives (those compilers honour them); this step is vkd3d-only.</para>
    /// </summary>
    public static string PrepareSource(string hlslSource) =>
        LineDirectivePattern.Replace(hlslSource, string.Empty);

    /// <summary>
    /// Resolves the shader profile for a request: <see cref="D3DCompileRequest.ProfileOverride"/>
    /// when set (the FNA SM ≤ 3 path), otherwise SM5 derived from the stage
    /// (vs_5_0/ps_5_0 — the MonoGame DX11 path).
    /// </summary>
    public static string ResolveProfile(D3DCompileRequest request) =>
        request.ProfileOverride ?? ShaderProfiles.DefaultDxbcProfile(request.Stage);

    /// <summary>
    /// Profile strings look like "vs_3_0" / "ps_2_b" / "vs_5_0": the digit after the
    /// first underscore is the shader-model major version. SM ≤ 3 selects the D3D9
    /// token-stream target; anything unparseable falls through to DXBC_TPF (vkd3d then
    /// rejects a bad profile with its own diagnostic — fail loudly, constraint 5).
    /// </summary>
    public static bool IsSm3OrBelow(string profile)
    {
        int underscore = profile.IndexOf('_');
        return underscore >= 0
            && underscore + 1 < profile.Length
            && profile[underscore + 1] is >= '1' and <= '3';
    }

    /// <summary>The vkd3d target type for a resolved profile (SM ≤ 3 → D3D_BYTECODE, else DXBC_TPF).</summary>
    public static int ResolveTargetType(string profile) =>
        IsSm3OrBelow(profile) ? TargetTypeD3dBytecode : TargetTypeDxbcTpf;

    /// <summary>The blob kind matching <see cref="ResolveTargetType"/> for a resolved profile.</summary>
    public static BlobKind ResolveBlobKind(string profile) =>
        IsSm3OrBelow(profile) ? BlobKind.D3dBytecode : BlobKind.Dxbc;

    // SM1-3 semantics on an SM4+ target: `fxc` accepts them (that is how every MonoGame
    // `.fx` written against SM3 still builds at ps_4_0), and vkd3d only does with
    // BACKWARD_COMPATIBILITY/MAP_SEMANTIC_NAMES. Without it vkd3d 2.1 rejects a user
    // semantic on a pixel-shader output outright ("E5013: Invalid semantic 'COLOR'") and
    // leaves a `POSITION` vertex output / pixel input as a plain user semantic instead of
    // SV_Position. FxPreParser rewrites the `) : COLOR<n>` RETURN semantic in RewriteToSm4
    // mode, but it deliberately cannot touch the same semantic on a struct FIELD (the
    // struct may be a VS output, where COLOR is legal), so the option is what covers a
    // `struct { float4 c : COLOR0; }` pixel-shader return.
    private static readonly Vkd3dCompileOption[] DxbcTpfOptions =
    [
        new()
        {
            Name  = Vkd3dCompileOptionName.BackwardCompatibility,
            Value = (uint)Vkd3dBackwardCompatibility.MapSemanticNames,
        },
    ];

    /// <summary>
    /// The <c>vkd3d_shader_compile_option</c> list EVERY host hands
    /// <c>vkd3d_shader_compile</c> for a target type: the desktop backend marshals it into
    /// <c>vkd3d_shader_compile_info.options</c>, the browser backend sends it (flattened,
    /// <see cref="FlattenCompileOptions"/>) through the JS shim into the WASM wrapper,
    /// which passes it on untouched. This is the ONLY place a compile option is chosen.
    /// A host that built its own list is how the browser came to pass none while the
    /// desktop passed <c>MAP_SEMANTIC_NAMES</c> (issue #295), so a new option goes here
    /// and nowhere else.
    ///
    /// <para><see cref="TargetTypeDxbcTpf"/>: <c>BACKWARD_COMPATIBILITY</c> =
    /// <c>MAP_SEMANTIC_NAMES</c>. <see cref="TargetTypeD3dBytecode"/> (and anything else):
    /// no options; on the SM1-3 target those ARE the native semantics.</para>
    /// </summary>
    public static IReadOnlyList<Vkd3dCompileOption> ResolveCompileOptions(int targetType) =>
        targetType == TargetTypeDxbcTpf ? DxbcTpfOptions : [];

    /// <summary>
    /// <paramref name="options"/> as consecutive (name, value) 32-bit pairs: the shape that
    /// crosses the browser's <c>[JSImport]</c> boundary and the wrapper's
    /// <c>sdw_vkd3d_compile_options</c> ABI, and that the corpus probes record. A value is
    /// an <c>unsigned int</c> at the C ABI; it is carried bit-for-bit.
    /// </summary>
    public static int[] FlattenCompileOptions(IReadOnlyList<Vkd3dCompileOption> options)
    {
        var flat = new int[options.Count * 2];
        for (int i = 0; i < options.Count; i++)
        {
            flat[2 * i]     = (int)options[i].Name;
            flat[2 * i + 1] = unchecked((int)options[i].Value);
        }

        return flat;
    }

    /// <summary>
    /// Maps a failed vkd3d compile's message text to the primary <see cref="ShaderError"/>,
    /// surfacing vkd3d's verbatim diagnostics (constraint 5): parse the MSVC-style
    /// diagnostic lines first (real file/line/column), falling back to an SD0212 error
    /// carrying the raw text (or <paramref name="noDiagnosticsFallback"/> when vkd3d
    /// emitted nothing at all).
    /// </summary>
    public static ShaderError MapCompileFailure(
        string messages,
        string sourceFileName,
        string noDiagnosticsFallback)
    {
        // Shared selection policy (Phase 53): the first error-severity parsed
        // diagnostic wins (vkd3d prints warnings before the fatal line), unparseable
        // text is surfaced verbatim as the message, and the COMPLETE text rides on
        // RawDiagnostics. SD0212 now fires only when vkd3d emitted no text at all.
        return D3DCompilerDiagnosticReformatter.SelectPrimary(
            messages,
            sourceFileName,
            noDiagnosticsFallback,
            fallbackCode: "SD0212");
    }

    /// <summary>
    /// Maps a SUCCESSFUL vkd3d compile's message text to its warnings, verbatim
    /// (constraint 5). vkd3d's message buffer is populated on success too (both hosts
    /// compile at <c>VKD3D_SHADER_LOG_WARNING</c>): <c>W5300 Implicit truncation of vector
    /// type</c>, <c>W5302 Unrecognized attribute</c>, and the like. Every host parses that
    /// text HERE and then relocates the result with <see cref="Vkd3dSourceLocator"/>, so
    /// <c>PlatformBlob.Warnings</c>, and the <c>CompiledShader.Warnings</c> the pipeline
    /// surfaces, are the same on the desktop and in the browser. The browser host used to
    /// read the text and drop it on success (issue #335): identical bytes, different
    /// warnings, which no byte-identity gate could see.
    /// </summary>
    /// <returns>
    /// The parsed diagnostics, every error-severity entry normalized to a warning (a
    /// successful compile cannot carry an error), with vkd3d's own coordinates; empty when
    /// vkd3d said nothing (whitespace counts as nothing). The caller relocates them.
    /// </returns>
    public static IReadOnlyList<ShaderError> MapCompileWarnings(string? messages, string sourceFileName) =>
        string.IsNullOrWhiteSpace(messages)
            ? []
            : D3DCompilerDiagnosticReformatter.ReformatAsWarnings(messages, sourceFileName);
}
