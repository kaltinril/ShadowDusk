#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>The dimensionality of a reflected texture binding.</summary>
public enum TextureDimension
{
    /// <summary>The dimension could not be determined.</summary>
    Unknown,
    /// <summary>A 1D texture.</summary>
    Texture1D,
    /// <summary>A 2D texture.</summary>
    Texture2D,
    /// <summary>A 3D (volume) texture.</summary>
    Texture3D,
    /// <summary>A cube-map texture.</summary>
    TextureCube,
}
