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

        try
        {
            // Self-register the [JSImport] modules (idempotent, zero consumer wiring),
            // then drive the one-time lazy load of the vkd3d WASM module. Loading is
            // deferred to first compile (the DXC pattern) so the module download never
            // burdens page init. This is the ONLY genuinely-async step (issue #28); the
            // compile itself is the synchronous core below.
            await WasmCompilerInitialization.EnsureVkd3dReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            return Result<PlatformBlob, ShaderError>.Fail(
                MapLoadFailure(ex, request.SourceFileName));
        }

        return Compile(request, cancellationToken);
    }

    /// <summary>
    /// Synchronous compile (issue #28): calls the synchronous <c>compile</c>
    /// <c>[JSImport]</c> directly. PRECONDITION: the vkd3d-shader module is loaded
    /// (<see cref="WasmCompilerInitialization.Vkd3dReady"/>) — when it is not, returns
    /// the clear SD1903 not-initialized error instead of risking an opaque runtime
    /// abort. Never awaits or blocks on a task.
    /// </summary>
    public Result<PlatformBlob, ShaderError> Compile(
        D3DCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!WasmCompilerInitialization.Vkd3dReady)
        {
            return Result<PlatformBlob, ShaderError>.Fail(
                WasmCompilerInitialization.NotInitializedError(
                    "vkd3d-shader (DirectX DXBC / FNA fx_2_0 backend)", request.SourceFileName));
        }

        // Shared contract — identical resolution to the desktop Vkd3dShaderCompiler.
        string profile     = Vkd3dCompileContract.ResolveProfile(request);
        int targetType     = Vkd3dCompileContract.ResolveTargetType(profile);
        BlobKind blobKind  = Vkd3dCompileContract.ResolveBlobKind(profile);

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

    /// <summary>
    /// The module genuinely is not loadable (not restored, not hosted, fetch failed).
    /// Fail loudly with a clear SD code — never silently, never a substitute compiler
    /// (the desktop SD0211 pattern, WASM flavor). Shared by the async compile path and
    /// <see cref="WasmShaderCompiler"/>'s warm-up so the failure maps identically.
    /// </summary>
    internal static ShaderError MapLoadFailure(JSException ex, string? sourceFileName) =>
        new(
            File:    sourceFileName ?? "<source>",
            Line:    0,
            Column:  0,
            Code:    "SD1902",
            Message: "WASM vkd3d-shader backend (vkd3d/vkd3d-shader.{js,wasm}) could not be " +
                     "loaded, so the DirectX (DXBC) and FNA (fx_2_0) targets are unavailable " +
                     "in this browser session. The module ships as a ShadowDusk.Wasm static " +
                     "web asset once restored (tools/restore.* / release tag " +
                     "native-vkd3d-wasm-1.17 — see src/ShadowDusk.Wasm/wwwroot/vkd3d/RESTORE.md). " +
                     "Underlying error: " + ex.Message);
}
