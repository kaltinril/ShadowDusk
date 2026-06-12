#nullable enable

using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.HLSL.Vkd3d;

/// <summary>
/// The PURE request→ABI mapping and error mapping shared by every vkd3d-shader
/// backend host — the desktop P/Invoke backend (<see cref="Vkd3dShaderCompiler"/>)
/// and the browser/WASM backend (<c>ShadowDusk.Wasm.WasmVkd3dShaderCompiler</c>, via
/// <c>InternalsVisibleTo</c>). Centralizing it here is what makes the two hosts
/// semantically one backend (Phase 4.1): same profile defaults, same SM ≤ 3 →
/// D3D_BYTECODE routing, same diagnostic fidelity — so the only difference between
/// hosts is HOW the native vkd3d call is made, never WHAT is asked of it.
///
/// <para>No I/O, no interop, no process — unit-testable per the conventions
/// (<c>Vkd3dCompileContractTests</c>).</para>
/// </summary>
internal static class Vkd3dCompileContract
{
    /// <summary>
    /// VKD3D_SHADER_TARGET_D3D_BYTECODE — the bare legacy D3D9 token stream
    /// (SM1–3, the FNA fx_2_0 path). Value pinned by the vkd3d 1.17 ABI and the
    /// Phase 4.1 WASM wrapper contract (<c>sdw_vkd3d_compile</c> target_type = 4);
    /// must equal <see cref="Vkd3dTargetType.D3dBytecode"/>.
    /// </summary>
    public const int TargetTypeD3dBytecode = 4;

    /// <summary>
    /// VKD3D_SHADER_TARGET_DXBC_TPF — the DXBC container MonoGame's DX11 runtime
    /// loads (SM4/5). Value pinned by the vkd3d 1.17 ABI and the Phase 4.1 WASM
    /// wrapper contract (<c>sdw_vkd3d_compile</c> target_type = 5); must equal
    /// <see cref="Vkd3dTargetType.DxbcTpf"/>.
    /// </summary>
    public const int TargetTypeDxbcTpf = 5;

    /// <summary>
    /// Resolves the shader profile for a request: <see cref="D3DCompileRequest.ProfileOverride"/>
    /// when set (the FNA SM ≤ 3 path), otherwise SM5 derived from the stage
    /// (vs_5_0/ps_5_0 — the MonoGame DX11 path).
    /// </summary>
    public static string ResolveProfile(D3DCompileRequest request) =>
        request.ProfileOverride ?? request.Stage switch
        {
            ShaderStage.Vertex => "vs_5_0",
            ShaderStage.Pixel  => "ps_5_0",
            _ => throw new ArgumentOutOfRangeException(
                nameof(request), $"Unsupported shader stage for DXBC: {request.Stage}"),
        };

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
        IReadOnlyList<ShaderError> errors =
            D3DCompilerDiagnosticReformatter.Reformat(messages, sourceFileName);

        if (errors.Count > 0)
            return errors[0];

        return new ShaderError(
            File:    sourceFileName,
            Line:    0,
            Column:  0,
            Code:    "SD0212",
            Message: string.IsNullOrWhiteSpace(messages)
                ? noDiagnosticsFallback
                : messages.Trim(),
            RawDiagnostics: string.IsNullOrWhiteSpace(messages) ? null : messages);
    }
}
