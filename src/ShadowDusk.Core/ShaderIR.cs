#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// ShadowDusk's backend-neutral intermediate representation of a whole effect, sitting
/// between the parsed/reflected HLSL and the final MGFX emission: the constant buffers,
/// per-pass compiled shader blobs, flattened effect parameters, and techniques the
/// <see cref="MgfxWriter"/> serializes. It carries no platform-specific bytecode shape,
/// so the same IR drives every MGFX-targeting backend.
/// </summary>
public sealed class ShaderIR
{
    public IReadOnlyList<ConstantBufferInfo>  ConstantBuffers { get; init; } = [];
    public IReadOnlyList<CompiledShaderBlob>  Shaders         { get; init; } = [];
    public IReadOnlyList<EffectParameterInfo> Parameters      { get; init; } = [];
    public IReadOnlyList<MgfxTechniqueInfo>   Techniques      { get; init; } = [];
}
