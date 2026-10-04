#nullable enable

using ShadowDusk.Core;
using Vortice.Dxc;
using static Vortice.Dxc.Dxc;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Compiles preprocessed HLSL to SPIR-V or DXBC via Vortice.Dxc.
/// NOT thread-safe: do not share across concurrent compilations.
/// Create one instance per parallel worker or serialize via a lock/channel.
/// </summary>
public sealed class DxcShaderCompiler : IDxcShaderCompiler, IDisposable
{
    private readonly IDxcCompiler3? _compiler;
    private readonly ShaderError? _loadError;
    private bool _disposed;

    /// <summary>
    /// Creates the DXC compiler instance from ShadowDusk's pinned DXC natives (on Windows,
    /// with the pinned <c>dxil.dll</c> validating and signing DXIL). If those natives cannot
    /// be guaranteed, every compile returns an <c>SD0219</c> error instead.
    /// </summary>
    public DxcShaderCompiler()
    {
        // Loads the pinned natives by absolute path, after checking they are the pinned build
        // (Windows/Linux/macOS; Android: bare SONAME from the APK), and answers Vortice's
        // resolver ahead of Vortice's own handler. Idempotent. Must precede the first DXC
        // P/Invoke below. The dlopen happens in there, outside DxcForkGate. Never call
        // Vortice's Dxc.LoadDxil(): it is a bare LoadLibrary("dxil.dll") that walks PATH and
        // let a foreign validator win.
        _loadError = DxcLoader.Register();
        if (_loadError is null)
            _compiler = CreateDxcCompiler<IDxcCompiler3>();
    }

    /// <summary>
    /// The <c>SD0219</c> error to return instead of compiling, or null. Missing, unloadable or
    /// foreign-build natives fail every request. A foreign DXIL validator fails only requests whose
    /// output it decides (<paramref name="usesValidator"/>: validated DXIL, i.e. DirectX 12):
    /// SPIR-V codegen, <c>-Vd</c> compiles and <c>-P</c> preprocessing never call it, so a host
    /// that loaded its own <c>dxil.dll</c> must not cost the consumer those targets. Checked once,
    /// before the native call: DXC binds its validator while its library loads (Windows
    /// <c>DllMain</c>; the Unix library constructor), never during a compile, so there is
    /// nothing to re-check afterwards.
    /// </summary>
    private ShaderError? NativeError(string? sourceFileName, bool usesValidator)
    {
        ShaderError? error = _loadError ?? (usesValidator ? DxcLoader.CheckBoundValidator() : null);
        return error is null ? null : error with { File = sourceFileName ?? "" };
    }

    /// <summary>
    /// True when DXC will run its DXIL validator (and, on Windows, signer) on this compile:
    /// DXIL output (no <c>-spirv</c>) without <c>-Vd</c>.
    /// </summary>
    internal static bool UsesValidator(IReadOnlyList<string> arguments) =>
        !arguments.Contains("-spirv") && !arguments.Contains("-Vd");

    /// <inheritdoc/>
    public Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        DxcCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => CompileRejectingWaveOps(request), cancellationToken);
    }

    /// <inheritdoc/>
    public Result<PlatformBlob, ShaderError> Compile(
        DxcCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CompileRejectingWaveOps(request);
    }

    /// <inheritdoc/>
    public Result<string, ShaderError> Preprocess(
        DxcPreprocessRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (NativeError(request.SourceFileName, usesValidator: false) is { } loadError)
            return Result<string, ShaderError>.Fail(loadError);

        IReadOnlyList<string> arguments = DxcFlagBuilder.BuildPreprocess(request.Macros);

        // Same raw vtable call the compile path uses (per-platform wchar_t arg encoding,
        // UTF-8 source). #includes are already flattened upstream, so no include handler.
        IDxcResult result = DxcNativeInterop.Compile(
            _compiler!,
            request.HlslSource,
            arguments,
            includeHandler: null);

        try
        {
            SharpGen.Runtime.Result status = result.GetStatus();
            string errorText = result.GetErrors();

            if (status.Failure)
            {
                return Result<string, ShaderError>.Fail(
                    DxcDiagnosticReformatter.SelectPrimary(
                        errorText,
                        request.SourceFileName,
                        noDiagnosticsFallback: "Shader preprocessing failed with no diagnostics"));
            }

            // -P writes the expanded HLSL text to the Hlsl output (not the Object blob).
            using IDxcBlob hlslBlob = result.GetOutput(DxcOutKind.Hlsl);
            byte[] bytes = hlslBlob.AsBytes();
            string text = System.Text.Encoding.UTF8.GetString(bytes);
            // DXC -P NUL-terminates the Hlsl blob; trim the trailing NUL(s) so the FX
            // re-parser's lexer does not flag the terminator as an unexpected character.
            text = text.TrimEnd('\0');
            return Result<string, ShaderError>.Ok(text);
        }
        finally
        {
            result.Dispose();
        }
    }

    // Issue #229: on the Vulkan target DXC refuses wave/quad intrinsics at its default Vulkan
    // 1.0 target env. ShadowDusk deliberately does NOT retry with -fspv-target-env=vulkan1.1:
    // MonoGame's DesktopVK creates a Vulkan 1.0 instance with no subgroup support, and the
    // SPIR-V 1.3 / GroupNonUniform module that flag produces was measured out of spec there by
    // the Khronos validation layer. OpenGL's fixed SM5 profile hits the same DXC rejection. Both
    // are relabelled (SD0218 / SD0624, shared with the real-slangc route, see
    // WaveQuadIntrinsics), keeping DXC's location, its message verbatim, and its raw text.
    private Result<PlatformBlob, ShaderError> CompileRejectingWaveOps(DxcCompileRequest request)
    {
        Result<PlatformBlob, ShaderError> result = CompileCore(request);

        if (result.IsSuccess)
            return result;

        ShaderError? relabelled = WaveQuadIntrinsics.Relabel(result.Error, request.Platform, "DXC");
        return relabelled is null ? result : Result<PlatformBlob, ShaderError>.Fail(relabelled);
    }

    private Result<PlatformBlob, ShaderError> CompileCore(DxcCompileRequest request)
    {
        IReadOnlyList<string> arguments = DxcFlagBuilder.Build(
            request.Platform,
            request.Stage,
            request.EntryPoint,
            request.Macros,
            request.Options);
        bool usesValidator = UsesValidator(arguments);

        if (NativeError(request.SourceFileName, usesValidator) is { } loadError)
            return Result<PlatformBlob, ShaderError>.Fail(loadError);

        // A SPIR-V compile with debug information loads libdxcompiler by LEAF name from inside
        // DXC to read the source for OpSource (issue #332). Refused (SD0223) when the dynamic
        // linker would hand that load anything but the pinned build; no other request makes it.
        if (DxcLeafNameLookup.CompileReadsSourceThroughLeafNameLoad(arguments)
            && DxcLoader.CheckDebugSpirvLookup() is { } lookupError)
        {
            return Result<PlatformBlob, ShaderError>.Fail(lookupError with { File = request.SourceFileName ?? "" });
        }

        // Raw vtable call instead of Vortice's IDxcCompiler3.Compile(string, string[], ...):
        // Vortice marshals the LPCWSTR* argument array as UTF-16 on every OS, but DXC's
        // non-Windows builds use the native 4-byte wchar_t — on Linux/macOS the compiler
        // reads garbage arguments and every compile fails with "Internal Compiler error:"
        // (Phase 37 Finding B). DxcNativeInterop encodes the arguments per-platform.
        IDxcResult result = DxcNativeInterop.Compile(
            _compiler!,
            request.HlslSource,
            arguments,
            request.IncludeHandler);

        // Dispose the native COM result (and the object blob below) deterministically —
        // historically neither was released, leaking native memory on EVERY compile.
        // The same pattern as D3DCompilerShaderCompiler's blob disposal.
        try
        {
            SharpGen.Runtime.Result status = result.GetStatus();
            string errorText = result.GetErrors();

            if (status.Failure)
            {
                // First error-severity diagnostic wins (never a leading warning), and
                // the COMPLETE verbatim DXC text rides on RawDiagnostics so the
                // single-error contract drops nothing — the surfaces print it.
                return Result<PlatformBlob, ShaderError>.Fail(
                    DxcDiagnosticReformatter.SelectPrimary(
                        errorText,
                        request.SourceFileName,
                        noDiagnosticsFallback: "Shader compilation failed with no diagnostics"));
            }

            // Copy the bytecode into a managed array BEFORE disposal: the previous
            // GetObjectBytecodeMemory() returned memory backed by the native blob, which
            // both pinned the blob alive forever and made the returned bytes unsafe to
            // outlive it. The managed copy is byte-identical.
            byte[] bytes;
            using (IDxcBlob objectBlob = result.GetOutput(DxcOutKind.Object))
            {
                bytes = objectBlob.AsBytes();
            }

            // Issue #343: a debug SPIR-V module carries no OpSource text read from the host's disk.
            Result<byte[], ShaderError> normalized = DxcDebugSpirvSource.Normalize(arguments, bytes, request.SourceFileName);
            if (normalized.IsFailure)
                return Result<PlatformBlob, ShaderError>.Fail(normalized.Error);
            bytes = normalized.Value;

            // DXC emits SM6 DXIL for both DirectX targets (the DirectX case here is the
            // reflection-only companion compile; vkd3d produces DX11's shipped DXBC) and
            // SPIR-V for the GL/Vulkan targets. These were mislabeled Dxbc/Spirv — no
            // consumer branched on them yet, but a future Kind-keyed dispatch would have
            // routed DXIL down a SPIR-V path (bug-hunt 2026-07-27 N10).
            BlobKind kind = request.Platform is PlatformTarget.DirectX or PlatformTarget.DirectX12
                ? BlobKind.Dxil
                : BlobKind.Spirv;

            // A successful compile can still carry diagnostic text (warnings — the
            // pipeline no longer forces -WX, matching mgfxc's fxc invocation, which
            // never passed /WX). Capture them verbatim instead of discarding; they
            // flow to CompiledShader.Warnings.
            IReadOnlyList<ShaderError> warnings = DxcDiagnosticReformatter.ReformatAsWarnings(
                errorText, request.SourceFileName);

            // DXIL signing (bug-hunt 2026-07-27 M7): dxil.dll validation/signing runs on
            // Windows only (this pin's Linux DXC never loads libdxil, and macOS ships no dxil at
            // all), so a DirectX12 compile on Linux/macOS produces UNSIGNED DXIL. That
            // loads only on machines with Developer Mode enabled — retail D3D12 rejects
            // unsigned DXIL at pipeline-state creation. Same source, different build
            // host, differently-broken artifact: surface it instead of shipping silently.
            if (request.Platform == PlatformTarget.DirectX12 && !OperatingSystem.IsWindows())
            {
                warnings =
                [
                    .. warnings,
                    new ShaderError(
                        File: request.SourceFileName,
                        Line: 0,
                        Column: 0,
                        Code: "SD0214",
                        Message: "DirectX12 DXIL compiled on a non-Windows host is unsigned " +
                                 "(dxil.dll validation/signing is Windows-only): the .mgfx " +
                                 "will load only with Windows Developer Mode enabled, and " +
                                 "retail D3D12 rejects it at pipeline-state creation. " +
                                 "Compile DirectX12 effects on Windows until cross-platform " +
                                 "signing ships.",
                        Severity: ShaderErrorSeverity.Warning),
                ];
            }

            return Result<PlatformBlob, ShaderError>.Ok(
                new PlatformBlob(kind, bytes) { Warnings = warnings });
        }
        finally
        {
            result.Dispose();
        }
    }

    /// <summary>Releases the native DXC compiler instance.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _compiler?.Dispose();
    }
}
