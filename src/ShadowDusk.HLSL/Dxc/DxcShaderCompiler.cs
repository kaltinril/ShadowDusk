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
    private readonly IDxcCompiler3 _compiler;
    private bool _disposed;

    /// <summary>
    /// Creates the DXC compiler instance, loading <c>dxil.dll</c> for DXIL validation on
    /// Windows (a no-op on other platforms).
    /// </summary>
    public DxcShaderCompiler()
    {
        // Load dxil.dll for DXIL validation on Windows; no-op on other platforms.
        LoadDxil();
        _compiler = CreateDxcCompiler<IDxcCompiler3>();
    }

    /// <inheritdoc/>
    public Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        DxcCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => CompileCore(request), cancellationToken);
    }

    private Result<PlatformBlob, ShaderError> CompileCore(DxcCompileRequest request)
    {
        IReadOnlyList<string> arguments = DxcFlagBuilder.Build(
            request.Platform,
            request.Stage,
            request.EntryPoint,
            request.Macros,
            request.Options);

        // IDxcIncludeHandler is declared non-nullable in Vortice but the native
        // API marks it Optional — null is safe when the source has no #includes.
        IDxcResult result = _compiler.Compile(
            request.HlslSource,
            arguments.ToArray(),
            request.IncludeHandler!);

        SharpGen.Runtime.Result status = result.GetStatus();
        string errorText = result.GetErrors();

        if (status.Failure)
        {
            IReadOnlyList<ShaderError> errors = DxcDiagnosticReformatter.Reformat(
                errorText,
                request.SourceFileName);

            ShaderError primary = errors.Count > 0
                ? errors[0]
                : new ShaderError(
                    File: request.SourceFileName,
                    Line: 0,
                    Column: 0,
                    Code: "X0000",
                    Message: "Shader compilation failed with no diagnostics",
                    Severity: ShaderErrorSeverity.Error,
                    RawDiagnostics: errorText);

            return Result<PlatformBlob, ShaderError>.Fail(primary);
        }

        ReadOnlyMemory<byte> bytes = result.GetObjectBytecodeMemory();

        BlobKind kind = request.Platform == PlatformTarget.DirectX
            ? BlobKind.Dxbc
            : BlobKind.Spirv;

        return Result<PlatformBlob, ShaderError>.Ok(new PlatformBlob(kind, bytes));
    }

    /// <summary>Releases the native DXC compiler instance.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _compiler.Dispose();
    }
}
