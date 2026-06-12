#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// One entry in a shader's sampler table, as MonoGame's <c>Shader</c> reader
/// expects it. <see cref="Name"/> must match the sampler uniform's name in the
/// emitted GLSL (e.g. <c>ps_s0</c>) so MonoGame's GL backend can bind it.
/// <see cref="Parameter"/> indexes the texture parameter in the effect's global
/// parameter table. <see cref="State"/> carries the baked
/// <c>sampler_state { MinFilter = …; AddressU = …; }</c> members (Phase 43, F9);
/// when null the record is written with <c>hasState = 0</c> and MonoGame uses
/// <c>GraphicsDevice.SamplerStates</c> instead.
/// </summary>
public sealed record MgfxSamplerInfo(
    byte   Type,
    byte   TextureSlot,
    byte   SamplerSlot,
    string Name,
    int    Parameter,
    MgfxSamplerStateInfo? State = null
);

/// <summary>
/// A baked sampler state, field-for-field as MonoGame 3.8.2's <c>Shader</c> reader
/// consumes it (AddressU/V/W, BorderColor RGBA, Filter, MaxAnisotropy, MaxMipLevel,
/// MipMapLevelOfDetailBias) and as mgfxc's <c>ShaderData.writer.cs</c> emits it.
/// Byte values are MonoGame enum ordinals (TextureAddressMode / TextureFilter).
/// </summary>
public sealed record MgfxSamplerStateInfo(
    byte  AddressU,
    byte  AddressV,
    byte  AddressW,
    byte  BorderColorR,
    byte  BorderColorG,
    byte  BorderColorB,
    byte  BorderColorA,
    byte  Filter,
    int   MaxAnisotropy,
    int   MaxMipLevel,
    float MipMapLevelOfDetailBias
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
