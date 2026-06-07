#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>A reflected texture binding: its name, bind slot, and dimensionality.</summary>
public sealed record TextureReflection
{
    /// <summary>The texture's name.</summary>
    public required string           Name      { get; init; }
    /// <summary>The texture's bind slot.</summary>
    public required int              BindSlot  { get; init; }
    /// <summary>The texture's dimensionality (2D, cube, 3D, …).</summary>
    public required TextureDimension Dimension { get; init; }
}
