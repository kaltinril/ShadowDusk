#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// The element type of an effect parameter, mirroring MonoGame's
/// <c>EffectParameterType</c> so reflected metadata maps directly onto the runtime.
/// </summary>
public enum EffectParameterType
{
    /// <summary><c>void</c> (no value).</summary>
    Void        = 0,
    /// <summary>Boolean.</summary>
    Bool        = 1,
    /// <summary>32-bit signed integer.</summary>
    Int32       = 2,
    /// <summary>32-bit floating point.</summary>
    Single      = 3,
    /// <summary>String value.</summary>
    String      = 4,
    /// <summary>An untyped texture.</summary>
    Texture     = 5,
    /// <summary>A 1D texture.</summary>
    Texture1D   = 6,
    /// <summary>A 2D texture.</summary>
    Texture2D   = 7,
    /// <summary>A 3D (volume) texture.</summary>
    Texture3D   = 8,
    /// <summary>A cube-map texture.</summary>
    TextureCube = 9,
}
