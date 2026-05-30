#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// One entry in a shader's sampler table, as MonoGame's <c>Shader</c> reader
/// expects it. <see cref="Name"/> must match the sampler uniform's name in the
/// emitted GLSL (e.g. <c>ps_s0</c>) so MonoGame's GL backend can bind it.
/// <see cref="Parameter"/> indexes the texture parameter in the effect's global
/// parameter table.
/// </summary>
public sealed record MgfxSamplerInfo(
    byte   Type,
    byte   TextureSlot,
    byte   SamplerSlot,
    string Name,
    int    Parameter
);

/// <summary>
/// One vertex-attribute table entry (GL profile only) mapping a GLSL attribute
/// to a <c>VertexElementUsage</c>+index, as MonoGame's <c>Shader</c> reader
/// expects for a vertex shader.
/// </summary>
public sealed record MgfxVertexAttributeInfo(
    string Name,
    byte   Usage,
    byte   Index,
    short  Location
);
