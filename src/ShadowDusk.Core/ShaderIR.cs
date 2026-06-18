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
    /// <summary>The effect's global constant buffers, in emission order.</summary>
    public IReadOnlyList<ConstantBufferInfo>  ConstantBuffers { get; init; } = [];

    /// <summary>The compiled per-pass shader blobs (vertex and pixel).</summary>
    public IReadOnlyList<CompiledShaderBlob>  Shaders         { get; init; } = [];

    /// <summary>The flattened effect parameters exposed to the runtime.</summary>
    public IReadOnlyList<EffectParameterInfo> Parameters      { get; init; } = [];

    /// <summary>The effect techniques, each with its ordered passes.</summary>
    public IReadOnlyList<MgfxTechniqueInfo>   Techniques      { get; init; } = [];
}
