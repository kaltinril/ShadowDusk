#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// The shape class of an effect parameter, mirroring MonoGame's
/// <c>EffectParameterClass</c> so reflected metadata maps directly onto the runtime.
/// </summary>
public enum EffectParameterClass
{
    /// <summary>A single scalar value.</summary>
    Scalar  = 0,
    /// <summary>A vector (e.g. <c>float4</c>).</summary>
    Vector  = 1,
    /// <summary>A matrix (e.g. <c>float4x4</c>).</summary>
    Matrix  = 2,
    /// <summary>An object such as a texture or sampler.</summary>
    Object  = 3,
    /// <summary>A user-defined struct.</summary>
    Struct  = 4,
}
