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
    /// signature. A reflection FAILURE is a compile-time error (bug-hunt 2026-07-27 M11):
    /// the previous empty-table fallback shipped exactly the delayed, unattributed
    /// <c>E_INVALIDARG</c> draw-time crash the class remarks describe. A shader that
    /// genuinely declares no vertex-buffer inputs still returns an empty table.
    /// </summary>
    public static Result<IReadOnlyList<MgfxVertexAttributeInfo>, ShaderError> Read(
        ReadOnlyMemory<byte> dxilBlob,
        DxilReflectionExtractor extractor)
    {
        Result<ReflectedEffect, ShaderError> result = extractor.Extract(dxilBlob);
        if (result.IsFailure)
            return Result<IReadOnlyList<MgfxVertexAttributeInfo>, ShaderError>.Fail(result.Error);

        var attributes = new List<MgfxVertexAttributeInfo>(result.Value.InputSignature.Count);
        foreach (SignatureParameterReflection param in result.Value.InputSignature)
        {
            // System-GENERATED values (SV_VertexID / SV_InstanceID) are produced by the
            // GPU, not fed from a vertex buffer. Mapping them through the unknown-semantic
            // fallback minted a phantom TEXCOORD attribute MonoGame then demanded from the
            // vertex declaration (bug-hunt 2026-07-27 M10). The SPIR-V path already skips
            // builtins the same way (SpirvReflectionParser: no Location => builtin).
            if (param.SemanticName.Equals("SV_VertexID", StringComparison.OrdinalIgnoreCase) ||
                param.SemanticName.Equals("SV_InstanceID", StringComparison.OrdinalIgnoreCase))
                continue;

            // DXIL reflection already separates the semantic name from its numeric index
            // (unlike SPIR-V's concatenated "TEXCOORD0" string), so only the name needs mapping.
            (byte usage, _) = VertexSemanticMapper.Map(param.SemanticName);
            attributes.Add(new MgfxVertexAttributeInfo(
                Name:     string.Empty,
                Usage:    usage,
                Index:    (byte)param.SemanticIndex,
                Location: 0));
        }

        return Result<IReadOnlyList<MgfxVertexAttributeInfo>, ShaderError>.Ok(attributes);
    }
}
