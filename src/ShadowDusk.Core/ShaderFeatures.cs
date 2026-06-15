#nullable enable

using System;

namespace ShadowDusk.Core;

/// <summary>
/// GL shader capabilities that ShadowDusk's MojoShader-dialect down-convert rejects today (every
/// shipping MonoGame/KNI GL runtime is MojoShader-capped at SM2-3). A <see cref="CapabilityProfile"/>
/// declares which it permits via <see cref="CapabilityProfile.AllowedFeatures"/>, but a feature is
/// only honored once a runtime is render-proven to consume it: <see cref="ShaderFeatureSupport"/>
/// rejects any feature no shipping runtime supports, so ShadowDusk never emits bytes no runtime can
/// load. <b>As of 2026-06 NONE of these are runtime-supported</b> (KNI's GL backend is still
/// MojoShader), so every proven profile declares <see cref="None"/>.
/// </summary>
[Flags]
public enum ShaderFeatures
{
    /// <summary>No extra features: every MojoShader-dialect rejection stays enforced (today's behavior).</summary>
    None = 0,

    /// <summary>Vertex texture fetch (sampling in the vertex stage). No shipping GL runtime consumes it yet.</summary>
    VertexTextureFetch = 1 << 0,

    /// <summary>Texture arrays (<c>Texture2DArray</c> and friends). No shipping GL runtime consumes it yet.</summary>
    TextureArrays = 1 << 1,

    /// <summary>Full fragment precision on GL ES (no MojoShader precision reduction). Reserved for a future runtime.</summary>
    FullPrecisionGLES = 1 << 2,
}
