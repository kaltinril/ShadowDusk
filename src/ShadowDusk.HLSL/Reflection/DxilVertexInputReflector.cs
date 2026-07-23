#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.HLSL.Reflection;

/// <summary>
/// Builds the <c>.mgfx</c> per-shader vertex ATTRIBUTE TABLE from a vertex shader's DXIL, for
/// the DirectX12 target. This table is NOT cosmetic on the new native backend: MonoGame's
/// managed <c>VertexInputLayout.GenerateInputElements</c> (shared by every native backend)
/// iterates it to build the D3D12 input layout — an empty table produces a zero-element
/// input layout with no reported error (the "missing input" check only runs inside a loop
/// over the table's own entries), which then fails <c>CreateGraphicsPipelineState</c> (called
/// lazily right before the first Draw) with <c>E_INVALIDARG</c>. Confirmed by reading
/// MonoGame's real v3.8.5 source directly (Phase 54 follow-up, 2026-07-23) — see
/// <c>VertexInputLayout.Native.cs</c> and <c>Shader.Native.cs</c>'s <c>GetOrCreateLayout</c>.
/// </summary>
public static class DxilVertexInputReflector
{
    /// <summary>
    /// Reads the vertex attribute table from <paramref name="dxilBlob"/>'s reflected input
    /// signature. Returns an empty list if reflection fails — never throws, so a reflection
    /// hiccup surfaces as a load/render failure MonoGame's own error message explains, not an
    /// opaque exception here.
    /// </summary>
    public static IReadOnlyList<MgfxVertexAttributeInfo> Read(
        ReadOnlyMemory<byte> dxilBlob,
        DxilReflectionExtractor extractor)
    {
        Result<ReflectedEffect, ShaderError> result = extractor.Extract(dxilBlob);
        if (result.IsFailure)
            return Array.Empty<MgfxVertexAttributeInfo>();

        var attributes = new List<MgfxVertexAttributeInfo>(result.Value.InputSignature.Count);
        foreach (SignatureParameterReflection param in result.Value.InputSignature)
        {
            // DXIL reflection already separates the semantic name from its numeric index
            // (unlike SPIR-V's concatenated "TEXCOORD0" string), so only the name needs mapping.
            (byte usage, _) = VertexSemanticMapper.Map(param.SemanticName);
            attributes.Add(new MgfxVertexAttributeInfo(
                Name:     string.Empty,
                Usage:    usage,
                Index:    (byte)param.SemanticIndex,
                Location: 0));
        }

        return attributes;
    }
}
