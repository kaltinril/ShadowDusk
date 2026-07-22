#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// A compiled platform shader blob plus a tag describing what kind of bytecode it holds.
/// Produced by the HLSL backends (<see cref="DxcShaderCompiler"/> for SPIR-V/DXIL, the
/// DXBC backends for SM5 DXBC and SM1-3 D3D9 bytecode) and consumed by the reflection and
/// effect-writing stages.
/// </summary>
public sealed class PlatformBlob
{
    /// <summary>What kind of bytecode <see cref="Bytes"/> contains.</summary>
    public BlobKind Kind { get; }

    /// <summary>The raw compiled bytecode.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>
    /// Non-fatal diagnostics the underlying compiler emitted while producing this
    /// blob, verbatim (all <see cref="ShaderErrorSeverity.Warning"/> /
    /// <see cref="ShaderErrorSeverity.Note"/> severity). Empty when the compiler was
    /// silent. The pipeline aggregates these into <c>CompiledShader.Warnings</c> —
    /// captured, never discarded (constraint 5).
    /// </summary>
    public IReadOnlyList<ShaderError> Warnings { get; init; } = Array.Empty<ShaderError>();

    /// <summary>Creates a blob of the given <paramref name="kind"/> wrapping <paramref name="bytes"/>.</summary>
    public PlatformBlob(BlobKind kind, ReadOnlyMemory<byte> bytes)
    {
        Kind = kind;
        Bytes = bytes;
    }
}
