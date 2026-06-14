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

    /// <summary>
    /// The HLSL shader model this blob was compiled from (Major, Minor), e.g. (3, 0)
    /// for <c>vs_3_0</c>. <b>MGFX v10 does not store this</b> (<see cref="MgfxWriter"/>
    /// ignores it); the <see cref="KnifxWriter"/> (KNIFX v11) serializes it per shader.
    /// Defaults to (3, 0) — the MojoShader GL ceiling and the common case; the
    /// compilation pipeline overrides it from the pass's profile string.
    /// </summary>
    public (int Major, int Minor) ShaderModel { get; init; } = (3, 0);
}
