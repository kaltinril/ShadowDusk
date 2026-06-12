#nullable enable

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Vkd3d;

namespace ShadowDusk.Wasm;

/// <summary>
/// Browser/WASM vkd3d-shader backend (Phase 4.1): the THIRD
/// <see cref="IDxbcShaderCompiler"/>, mirroring the desktop
/// <see cref="Vkd3dShaderCompiler"/> one-for-one behind a <c>[JSImport]</c> boundary
/// instead of P/Invoke. It is the SAME pinned vkd3d-shader 1.17 compiled to
/// WebAssembly (NO substitute compiler), so the bytes it produces are asserted
/// byte-identical to the desktop backend's (the Phase 23 G1-gate pattern:
/// <c>tests/ShadowDusk.BrowserTests/node-test-vkd3d-wasm.mjs</c>).
///
/// <para>One artifact closes two cells: SM4/5 → DXBC_TPF serves the browser
/// <see cref="PlatformTarget.DirectX"/> export, SM ≤ 3 → D3D_BYTECODE serves the
/// browser <see cref="PlatformTarget.Fna"/> fx_2_0 export (the
/// <c>Fx2EffectWriter</c> / <c>D3d9BytecodePatcher</c> / CTAB reflection around it
/// are managed C# that already runs in WASM).</para>
///
/// <para>Request→ABI mapping and error mapping are the SHARED
/// <see cref="Vkd3dCompileContract"/> — same profile defaults (vs_5_0/ps_5_0),
/// same SM ≤ 3 routing, same verbatim-diagnostic surfacing (constraint 5) as the
/// desktop backend. When the WASM module itself cannot be loaded (e.g.
/// <c>vkd3d-shader.wasm</c> not restored/hosted yet) the compile fails loudly with
/// <c>SD1902</c> — the WASM sibling of the desktop's SD0211 native-not-found.</para>
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class WasmVkd3dShaderCompiler : IDxbcShaderCompiler
{
    /// <inheritdoc/>
    public async Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        D3DCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Shared contract — identical resolution to the desktop Vkd3dShaderCompiler.
        string profile     = Vkd3dCompileContract.ResolveProfile(request);
        int targetType     = Vkd3dCompileContract.ResolveTargetType(profile);
        BlobKind blobKind  = Vkd3dCompileContract.ResolveBlobKind(profile);

        try
        {
            // Self-register the [JSImport] modules (idempotent, zero consumer wiring),
            // then drive the one-time lazy load of the vkd3d WASM module. Loading is
            // deferred to first compile (the DXC pattern) so the module download never
            // burdens page init.
            await WasmModuleRegistration.EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
            await Vkd3dInterop.EnsureReadyAsync().ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            // The module genuinely is not loadable (not restored, not hosted, fetch
            // failed). Fail loudly with a clear SD code — never silently, never a
            // substitute compiler (the desktop SD0211 pattern, WASM flavor).
            return Result<PlatformBlob, ShaderError>.Fail(new ShaderError(
                File:    request.SourceFileName,
                Line:    0,
                Column:  0,
                Code:    "SD1902",
                Message: "WASM vkd3d-shader backend (vkd3d/vkd3d-shader.{js,wasm}) could not be " +
                         "loaded, so the DirectX (DXBC) and FNA (fx_2_0) targets are unavailable " +
                         "in this browser session. The module ships as a ShadowDusk.Wasm static " +
                         "web asset once restored (tools/restore.* / release tag " +
                         "native-vkd3d-wasm-1.17 — see src/ShadowDusk.Wasm/wwwroot/vkd3d/RESTORE.md). " +
                         "Underlying error: " + ex.Message));
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Source bytes are UTF-8 and NOT null-terminated (vkd3d_shader_code carries
            // bytes + length); entry/profile/source-name cross as C strings inside the
            // JS glue. Mirrors the desktop marshalling exactly.
            byte[] sourceBytes = Encoding.UTF8.GetBytes(request.HlslSource);

            byte[] code = Vkd3dInterop.Compile(
                sourceBytes,
                request.EntryPoint,
                profile,
                request.SourceFileName,
                targetType);

            return Result<PlatformBlob, ShaderError>.Ok(new PlatformBlob(blobKind, code));
        }
        catch (JSException ex)
        {
            // The JS shim re-throws vkd3d's VERBATIM messages as the exception message
            // (constraint 5). Map them with the SAME shared reformatter the desktop
            // backend uses, so the in-browser failure carries real file/line/column.
            return Result<PlatformBlob, ShaderError>.Fail(
                Vkd3dCompileContract.MapCompileFailure(
                    ex.Message,
                    request.SourceFileName,
                    "vkd3d-shader WASM compilation failed with no diagnostics"));
        }
    }
}
