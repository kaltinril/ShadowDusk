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
    /// <summary>
    /// The raw SPIR-V <c>Binding</c> decoration (unlike <see cref="BindSlot"/>, which is a
    /// class-relative renumbering for the DXIL-oracle-equivalent GL/DX record shape). Used
    /// only by the Vulkan container's descriptor-layout table. Default 0 for non-Vulkan.
    /// </summary>
    public int RawBinding { get; init; }
    /// <summary>The raw SPIR-V <c>DescriptorSet</c> decoration. See <see cref="RawBinding"/>.</summary>
    public int RawDescriptorSet { get; init; }
}
