#nullable enable

namespace ShadowDusk.Core;

public sealed record CompiledShaderBlob(
    byte[]      Bytes,
    ShaderStage Stage
)
{
    /// <summary>Sampler table for this shader (empty if it samples nothing).</summary>
    public IReadOnlyList<MgfxSamplerInfo> Samplers { get; init; } = [];

    /// <summary>Indices (into the effect's global constant-buffer list) this shader uses.</summary>
    public IReadOnlyList<int> ConstantBufferIndices { get; init; } = [];

    /// <summary>
    /// Vertex-attribute table. Populated for GL vertex shaders only; empty for
    /// DirectX (which binds vertex inputs via the DXBC input signature). The count
    /// byte is still written on both profiles — see <see cref="MgfxWriter"/>.
    /// </summary>
    public IReadOnlyList<MgfxVertexAttributeInfo> Attributes { get; init; } = [];
}
