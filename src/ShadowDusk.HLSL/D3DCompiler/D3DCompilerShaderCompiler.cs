#nullable enable

using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace ShadowDusk.HLSL.D3DCompiler;

/// <summary>
/// Compiles preprocessed HLSL to SM5 DXBC via d3dcompiler_47.dll (the fxc
/// engine). This is the Windows-only "oracle" DXBC backend (Phase 18): it emits
/// the real Shader-Model-5 bytecode MonoGame's DX11 runtime loads, which DXC
/// cannot produce (DXC's minimum is SM6 DXIL). A cross-platform vkd3d-shader
/// backend is intended to replace it behind <see cref="IDxbcShaderCompiler"/>.
///
/// Off Windows the package restores fine (managed wrapper) but the native
/// d3dcompiler_47.dll is absent; <see cref="CompileAsync"/> returns a clear
/// <see cref="ShaderError"/> rather than throwing DllNotFoundException.
/// </summary>
public sealed class D3DCompilerShaderCompiler : IDxbcShaderCompiler
{
    public Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        D3DCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Guard failures short-circuit without a thread hop (the pre-existing behavior).
        if (GuardError(request) is { } guardError)
            return Task.FromResult(Result<PlatformBlob, ShaderError>.Fail(guardError));

        return Task.Run(() => CompileCore(request), cancellationToken);
    }

    /// <inheritdoc/>
    public Result<PlatformBlob, ShaderError> Compile(
        D3DCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (GuardError(request) is { } guardError)
            return Result<PlatformBlob, ShaderError>.Fail(guardError);

        return CompileCore(request);
    }

    /// <summary>
    /// Shared policy guards for both entry points. ProfileOverride is the vkd3d
    /// backend's SM1–3 (FNA) hook — this oracle never serves that path; honoring it here
    /// would let output silently depend on which backend a host picked. Refuse loudly,
    /// and BEFORE the Windows guard: the refusal is platform-independent policy, and
    /// checking it first makes it unit-testable on every OS.
    /// </summary>
    private static ShaderError? GuardError(D3DCompileRequest request)
    {
        if (request.ProfileOverride is not null)
        {
            return new ShaderError(
                File:    request.SourceFileName,
                Line:    0,
                Column:  0,
                Code:    "SD0210",
                Message: $"The d3dcompiler_47 oracle backend does not support ProfileOverride " +
                         $"('{request.ProfileOverride}') — SM1–3 compiles route through the " +
                         "vkd3d-shader backend");
        }

        if (!OperatingSystem.IsWindows())
        {
            return new ShaderError(
                File:    request.SourceFileName,
                Line:    0,
                Column:  0,
                Code:    "SD0210",
                Message: "DXBC oracle backend requires Windows; use DxbcBackend.Vkd3d for cross-platform DXBC");
        }

        return null;
    }

    private static Result<PlatformBlob, ShaderError> CompileCore(D3DCompileRequest request)
    {
        // Re-assert inside the worker so the platform analyzer (CA1416) sees the
        // guard on this call path; CompileAsync already returns early off-Windows.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DXBC oracle backend requires Windows");

        string profile = ShaderProfiles.DefaultDxbcProfile(request.Stage);

        // Row-major matrix packing matches the DXC path's -Zpr so reflection
        // offsets and the runtime's float4x4 layout agree across backends.
        ShaderFlags flags = ShaderFlags.PackMatrixRowMajor;
        if (!request.AllowWarnings)
            flags |= ShaderFlags.WarningsAreErrors;
        if (request.EmbedDebugInfo)
            flags |= ShaderFlags.Debug | ShaderFlags.SkipOptimization;

        // No defines / include handler: the preprocessor has already flattened
        // #includes and applied platform macros before reaching this backend.
        SharpGen.Runtime.Result status = Compiler.Compile(
            request.HlslSource,
            defines:    Array.Empty<ShaderMacro>(),
            include:    null!,
            entryPoint: request.EntryPoint,
            sourceName: request.SourceFileName,
            profile:    profile,
            shaderFlags: flags,
            effectFlags: EffectFlags.None,
            out Blob? code,
            out Blob? errorBlob);

        try
        {
            string errorText = errorBlob?.AsString() ?? string.Empty;

            if (status.Failure || code is null)
            {
                IReadOnlyList<ShaderError> errors =
                    D3DCompilerDiagnosticReformatter.Reformat(errorText, request.SourceFileName);

                ShaderError primary = errors.Count > 0
                    ? errors[0]
                    : new ShaderError(
                        File:    request.SourceFileName,
                        Line:    0,
                        Column:  0,
                        Code:    "X0000",
                        Message: "DXBC compilation failed with no diagnostics",
                        RawDiagnostics: errorText);

                return Result<PlatformBlob, ShaderError>.Fail(primary);
            }

            byte[] dxbc = code.AsBytes();
            return Result<PlatformBlob, ShaderError>.Ok(new PlatformBlob(BlobKind.Dxbc, dxbc));
        }
        finally
        {
            code?.Dispose();
            errorBlob?.Dispose();
        }
    }
}
