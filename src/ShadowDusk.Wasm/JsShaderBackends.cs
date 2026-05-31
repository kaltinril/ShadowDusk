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
internal sealed partial class JsDxcShaderCompiler : IDxcShaderCompiler
{
    public Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        DxcCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
            return Task.FromResult(Result<PlatformBlob, ShaderError>.Ok(blob));
        }
        catch (JSException ex)
        {
            return Task.FromResult(Result<PlatformBlob, ShaderError>.Fail(new ShaderError(
                File: request.SourceFileName,
                Line: 0,
                Column: 0,
                Code: "SD1900",
                Message: $"WASM DXC backend failed: {ex.Message}",
                Severity: ShaderErrorSeverity.Error,
                RawDiagnostics: ex.Message)));
        }
    }
}

/// <summary>
/// Browser-side SPIR-V → GLSL backend. Hands SPIR-V bytes plus the SAME SPIRV-Cross
/// option set the desktop <c>SpirvCrossGlslTranspiler</c> installs to a host-provided
/// JavaScript function backed by a WASM-compiled SPIRV-Cross, and wraps the returned
/// GLSL text in a <see cref="GlslSource"/>.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class JsSpirvToGlslTranspiler : ISpirvToGlslTranspiler
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
/// <c>[JSImport]</c> bindings into the host-provided DXC JavaScript module.
/// The host must register a module named <c>shadowdusk-dxc</c> exporting
/// <c>compileToSpirv(string hlsl, string[] args) =&gt; Uint8Array</c>.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class DxcInterop
{
    /// <summary>
    /// Compiles HLSL to a SPIR-V byte stream.
    /// JS contract: <c>compileToSpirv(hlslSource: string, args: string[]): Uint8Array</c>.
    /// On failure the JS side must throw (surfaced here as a <see cref="JSException"/>).
    /// </summary>
    [JSImport("compileToSpirv", "shadowdusk-dxc")]
    public static partial byte[] CompileToSpirv(string hlslSource, string[] args);
}

/// <summary>
/// <c>[JSImport]</c> bindings into the host-provided SPIRV-Cross JavaScript module.
/// The host must register a module named <c>shadowdusk-spirv-cross</c> exporting
/// <c>transpileToGlsl(spirv, flipVertexY, fixupDepthConvention, glslVersion, glslEs, vulkanSemantics) =&gt; string</c>.
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
