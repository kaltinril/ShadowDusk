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
        => Read(dxilBlob, extractor, out _);

    /// <summary>
    /// The same read as <see cref="Read(ReadOnlyMemory{byte}, DxilReflectionExtractor)"/>,
    /// additionally reporting the non-fatal <c>SD0104</c> warnings for input semantics that
    /// fell through to the TextureCoordinate default (bug-hunt 2026-07-27 N5 — mgfxc warns
    /// when it defaults, so a drop-in replacement must too). The attribute table is
    /// byte-for-byte what the warning-free overload produces: warnings never gate output.
    /// </summary>
    public static Result<IReadOnlyList<MgfxVertexAttributeInfo>, ShaderError> Read(
        ReadOnlyMemory<byte> dxilBlob,
        DxilReflectionExtractor extractor,
        out IReadOnlyList<ShaderError> warnings)
    {
        warnings = Array.Empty<ShaderError>();

        Result<ReflectedEffect, ShaderError> result = extractor.Extract(dxilBlob);
        if (result.IsFailure)
            return Result<IReadOnlyList<MgfxVertexAttributeInfo>, ShaderError>.Fail(result.Error);

        var attributes = new List<MgfxVertexAttributeInfo>(result.Value.InputSignature.Count);
        List<ShaderError>? unrecognized = null;
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
            (byte usage, _) = VertexSemanticMapper.Map(param.SemanticName, out bool recognized);
            // SD0104 (bug-hunt 2026-07-27 N5): mgfxc warns when it defaults an unrecognized
            // semantic, so we do too. The VALUE is untouched — the phantom TextureCoordinate
            // attribute is still written, exactly as mgfxc writes it. The reported name is
            // the reflected one, i.e. index-free ("TEXCORD", index 0), because DXIL splits
            // the two; the SPIR-V path reports the concatenated spelling ("TEXCORD0").
            if (!recognized)
            {
                unrecognized ??= new List<ShaderError>();
                unrecognized.Add(VertexSemanticMapper.UnrecognizedSemanticWarning(
                    param.SemanticName, param.SemanticIndex));
            }

            attributes.Add(new MgfxVertexAttributeInfo(
                Name:     string.Empty,
                Usage:    usage,
                Index:    (byte)param.SemanticIndex,
                Location: 0));
        }

        if (unrecognized is not null)
            warnings = unrecognized;

        return Result<IReadOnlyList<MgfxVertexAttributeInfo>, ShaderError>.Ok(attributes);
    }
}
