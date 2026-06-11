#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.HLSL.Reflection;

/// <summary>
/// D3D11 (DXBC / Shader-Model-5) analogue of <see cref="DxilReflectionExtractor"/>.
/// Reflects SM5 DXBC bytecode via the pure-managed <see cref="RdefReader"/> (Phase 18
/// Track A) and produces the SAME <see cref="ReflectedEffect"/> shape the DXIL path
/// produces, so the downstream cbuffer/parameter assembly and MGFX writer run unchanged.
///
/// Until Track A this P/Invoked d3dcompiler_47's <c>D3DReflect</c>
/// (<c>ID3D11ShaderReflection</c>), making DX11 reflection Windows-only (SD0210).
/// The managed reader runs identically on every OS and is deterministic; <c>D3DReflect</c>
/// is retained only as a Windows test oracle (<c>DxbcReflectionParityTests</c> asserts
/// deep equality of the two on both d3dcompiler_47 and vkd3d DXBC).
/// </summary>
public sealed class DxbcReflectionExtractor
{
    /// <summary>
    /// Reflects an SM5 DXBC module into a <see cref="ReflectedEffect"/>. Pure managed —
    /// runs on every OS.
    /// </summary>
    /// <param name="dxbcBlob">A complete SM5 DXBC module.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The reflected effect on success, or a <see cref="ShaderError"/> on failure.</returns>
    public Result<ReflectedEffect, ShaderError> Extract(
        ReadOnlyMemory<byte> dxbcBlob,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return RdefReader.Read(dxbcBlob.Span);
    }
}
