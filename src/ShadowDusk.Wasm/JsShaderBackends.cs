#nullable enable

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using ShadowDusk.Core;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.Wasm;

/// <summary>
/// Browser-side DXC backend. Builds the SAME DXC argument list the desktop
/// <c>DxcShaderCompiler</c> uses (via <see cref="DxcFlagBuilder"/>), then hands the
/// HLSL source and arguments to a host-provided JavaScript function backed by a
/// WASM-compiled DXC. The returned SPIR-V bytes are wrapped in a <see cref="PlatformBlob"/>.
///
/// <para>When a reflector is injected on the OpenGL target (the WASM path), the
/// pipeline reflects SPIR-V directly and SKIPS the DXIL compile, so this backend is
/// only ever asked for OpenGL/SPIR-V compiles — never DirectX/DXIL.</para>
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class JsDxcShaderCompiler : IDxcShaderCompiler
{
    public async Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        DxcCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // One-time load of registration (both the shadowdusk-dxc and
            // shadowdusk-spirv-cross [JSImport] modules — zero consumer wiring) + the
            // heavy ~17.4 MB DXC WASM. Awaited HERE, lazily, so the download is never
            // forced at page init (keeping the mode-1 boot instant). Idempotent. This is
            // the ONLY genuinely-async step; the compile itself is the synchronous core
            // below (issue #28) — so async and sync output is identical by construction.
            await WasmCompilerInitialization.EnsureDxcReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            return Result<PlatformBlob, ShaderError>.Fail(MapJsException(ex, request.SourceFileName));
        }

        return Compile(request, cancellationToken);
    }

    /// <summary>
    /// Synchronous compile (issue #28): calls the synchronous <c>compileToSpirv</c>
    /// <c>[JSImport]</c> directly. PRECONDITION: the DXC module is loaded
    /// (<see cref="WasmCompilerInitialization.DxcReady"/>) — when it is not, returns the
    /// clear SD1903 not-initialized error instead of risking an opaque runtime abort.
    /// Never awaits or blocks on a task.
    /// </summary>
    public Result<PlatformBlob, ShaderError> Compile(
        DxcCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!WasmCompilerInitialization.DxcReady)
        {
            return Result<PlatformBlob, ShaderError>.Fail(
                WasmCompilerInitialization.NotInitializedError(
                    "DXC (HLSL → SPIR-V frontend)", request.SourceFileName));
        }

        IReadOnlyList<string> arguments = DxcFlagBuilder.Build(
            request.Platform,
            request.Stage,
            request.EntryPoint,
            request.Macros,
            request.Options);

        try
        {
            byte[] spirv = DxcInterop.CompileToSpirv(request.HlslSource, arguments.ToArray());
            var blob = new PlatformBlob(BlobKind.Spirv, spirv);
            return Result<PlatformBlob, ShaderError>.Ok(blob);
        }
        catch (JSException ex)
        {
            return Result<PlatformBlob, ShaderError>.Fail(MapJsException(ex, request.SourceFileName));
        }
    }

    /// <summary>
    /// Phase 38: the JS shim re-throws DXC's VERBATIM diagnostics as the exception
    /// message (file:line:col: error: message). Parse it with the SAME reformatter the
    /// desktop path uses so the in-browser failure carries real line/column — a
    /// downstream editor (e.g. an XNA/KNI fiddle) can then squiggle the exact offending
    /// line instead of showing an opaque blob. Shared by the load and compile failure
    /// paths (and by <see cref="WasmShaderCompiler"/>'s warm-up) so every DXC-side
    /// JSException maps identically.
    /// </summary>
    internal static ShaderError MapJsException(JSException ex, string sourceFileName)
    {
        IReadOnlyList<ShaderError> parsed =
            DxcDiagnosticReformatter.Reformat(ex.Message, sourceFileName);

        // Prefer a located diagnostic (has a source line); fall back to the first
        // parsed entry, then to an explicit backend error carrying the raw text.
        ShaderError? located = null;
        foreach (ShaderError e in parsed)
        {
            if (e.Line > 0)
            {
                located = e;
                break;
            }
        }

        return located
            ?? (parsed.Count > 0
                ? parsed[0]
                : new ShaderError(
                    File: sourceFileName,
                    Line: 0,
                    Column: 0,
                    Code: "SD1900",
                    Message: $"WASM DXC backend failed: {ex.Message}",
                    Severity: ShaderErrorSeverity.Error,
                    RawDiagnostics: ex.Message));
    }
}

/// <summary>
/// Browser-side SPIR-V → GLSL backend. Hands SPIR-V bytes plus the SAME SPIRV-Cross
/// option set the desktop <c>SpirvCrossGlslTranspiler</c> installs to a host-provided
/// JavaScript function backed by a WASM-compiled SPIRV-Cross, and wraps the returned
/// GLSL text in a <see cref="GlslSource"/>.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed class JsSpirvToGlslTranspiler : ISpirvToGlslTranspiler
{
    public Result<GlslSource, ShaderError> Transpile(
        ReadOnlyMemory<byte> spirvBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Option values MUST match SpirvCrossGlslTranspiler exactly so the
            // browser-emitted GLSL is identical to the desktop output.
            string glsl = SpirvCrossInterop.TranspileToGlsl(
                spirvBytes.ToArray(),
                flipVertexY: true,
                fixupDepthConvention: true,
                glslVersion: 140,
                glslEs: false,
                vulkanSemantics: false);

            return Result<GlslSource, ShaderError>.Ok(new GlslSource(glsl));
        }
        catch (JSException ex)
        {
            return Result<GlslSource, ShaderError>.Fail(new ShaderError(
                File: "<spirv-cross>",
                Line: 0,
                Column: 0,
                Code: "SD1901",
                Message: $"WASM SPIRV-Cross backend failed: {ex.Message}"));
        }
    }
}

/// <summary>
/// <c>[JSImport]</c> bindings into the faithful DXC JavaScript module. The module
/// (<c>shadowdusk-dxc</c>, the faithful pinned DXC→WASM frontend) is self-registered by
/// <see cref="WasmModuleRegistration"/> from the package's own
/// <c>_content/ShadowDusk.Wasm/</c> static web assets — the consumer wires nothing. It
/// exports <c>compileToSpirv(string hlsl, string[] args) =&gt; Uint8Array</c>.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class DxcInterop
{
    /// <summary>
    /// Lazily loads and initializes the faithful WASM HLSL→SPIR-V backend (pinned
    /// DXC→WASM). The host must <c>await</c> this once before the first
    /// <see cref="CompileToSpirv"/>; it is idempotent and resolves immediately once
    /// loaded. Awaiting it here (rather than blocking the module's evaluation) keeps the
    /// ~17.4 MB WASM off the page-init critical path so the mode-1 render boots instantly.
    /// JS contract: <c>ensureReady(): Promise&lt;void&gt;</c> (rejects on load failure).
    /// </summary>
    [JSImport("ensureReady", "shadowdusk-dxc")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    public static partial Task EnsureReadyAsync();

    /// <summary>
    /// Compiles HLSL to a SPIR-V byte stream.
    /// JS contract: <c>compileToSpirv(hlslSource: string, args: string[]): Uint8Array</c>.
    /// On failure the JS side must throw (surfaced here as a <see cref="JSException"/>).
    /// </summary>
    [JSImport("compileToSpirv", "shadowdusk-dxc")]
    public static partial byte[] CompileToSpirv(string hlslSource, string[] args);
}

/// <summary>
/// <c>[JSImport]</c> bindings into the faithful vkd3d-shader JavaScript module
/// (Phase 4.1). The module (<c>shadowdusk-vkd3d</c>, the pinned vkd3d 1.17 compiled to
/// WASM) is self-registered by <see cref="WasmModuleRegistration"/> from the package's
/// own <c>_content/ShadowDusk.Wasm/</c> static web assets — the consumer wires nothing.
/// Like the DXC module it loads LAZILY: registration evaluates only the tiny committed
/// shim (<c>shadowdusk-vkd3d.js</c>); the restored <c>vkd3d/vkd3d-shader.{js,wasm}</c>
/// download + instantiate on the first <see cref="EnsureReadyAsync"/>.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class Vkd3dInterop
{
    /// <summary>
    /// Lazily loads and initializes the faithful vkd3d-shader→WASM backend. The host
    /// must <c>await</c> this once before the first <see cref="Compile"/>; idempotent,
    /// resolves immediately once loaded, rejects (→ <see cref="JSException"/>, mapped to
    /// SD1902) when the module is not loadable (e.g. not restored yet).
    /// JS contract: <c>ensureReady(): Promise&lt;void&gt;</c>.
    /// </summary>
    [JSImport("ensureReady", "shadowdusk-vkd3d")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    public static partial Task EnsureReadyAsync();

    /// <summary>
    /// Compiles HLSL (UTF-8 bytes, NOT null-terminated) to D3D bytecode via the
    /// <c>sdw_vkd3d_compile</c> C ABI. <paramref name="targetType"/> is the raw vkd3d
    /// target type (4 = D3D_BYTECODE for SM1–3/FNA, 5 = DXBC_TPF for SM4/5/DX11 —
    /// <c>Vkd3dCompileContract</c>). JS contract:
    /// <c>compile(source: Uint8Array, entryPoint: string, profile: string,
    /// sourceName: string, targetType: number): Uint8Array</c>; on failure the JS side
    /// throws an <c>Error</c> whose message carries vkd3d's VERBATIM diagnostics
    /// (surfaced here as a <see cref="JSException"/>).
    /// </summary>
    [JSImport("compile", "shadowdusk-vkd3d")]
    public static partial byte[] Compile(
        byte[] sourceUtf8,
        string entryPoint,
        string profile,
        string sourceName,
        [JSMarshalAs<JSType.Number>] int targetType);
}

/// <summary>
/// <c>[JSImport]</c> bindings into the SPIRV-Cross JavaScript module. The module
/// (<c>shadowdusk-spirv-cross</c>) is self-registered by
/// <see cref="WasmModuleRegistration"/> from the package's own
/// <c>_content/ShadowDusk.Wasm/</c> static web assets — the consumer wires nothing. It
/// exports <c>transpileToGlsl(spirv, flipVertexY, fixupDepthConvention, glslVersion, glslEs, vulkanSemantics) =&gt; string</c>.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class SpirvCrossInterop
{
    /// <summary>
    /// Transpiles a SPIR-V module to GLSL text.
    /// JS contract:
    /// <c>transpileToGlsl(spirv: Uint8Array, flipVertexY: boolean, fixupDepthConvention: boolean,
    /// glslVersion: number, glslEs: boolean, vulkanSemantics: boolean): string</c>.
    /// On failure the JS side must throw (surfaced here as a <see cref="JSException"/>).
    /// </summary>
    [JSImport("transpileToGlsl", "shadowdusk-spirv-cross")]
    public static partial string TranspileToGlsl(
        byte[] spirv,
        [JSMarshalAs<JSType.Boolean>] bool flipVertexY,
        [JSMarshalAs<JSType.Boolean>] bool fixupDepthConvention,
        [JSMarshalAs<JSType.Number>] int glslVersion,
        [JSMarshalAs<JSType.Boolean>] bool glslEs,
        [JSMarshalAs<JSType.Boolean>] bool vulkanSemantics);
}
