#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>A reflected texture binding: its name, bind slot, and dimensionality.</summary>
public sealed record TextureReflection
{
    /// <summary>The texture's name.</summary>
    public required string           Name      { get; init; }
    /// <summary>
    /// The texture's bind slot. A DXIL or DXBC reflection reports the register (<c>t1</c> is 1).
    /// The SPIR-V reflector reports the texture's position among the stage's textures in
    /// binding order (0, 1, 2, ...); on the Vulkan target the compiler replaces that with
    /// <c>RawBinding - 32</c>, the slot mgfxc writes and MonoGame's Vulkan runtime reads, which
    /// is the register again.
    /// </summary>
    public required int              BindSlot  { get; init; }
    /// <summary>The texture's dimensionality (2D, cube, 3D, …).</summary>
    public required TextureDimension Dimension { get; init; }
    /// <summary>
    /// The raw SPIR-V <c>Binding</c> decoration (unlike <see cref="BindSlot"/>). Used by the
    /// Vulkan target, for the container's descriptor-layout table and to derive the slot.
    /// Default 0 for a DXIL or DXBC reflection.
    /// </summary>
    public int RawBinding { get; init; }
    /// <summary>The raw SPIR-V <c>DescriptorSet</c> decoration. See <see cref="RawBinding"/>.</summary>
    public int RawDescriptorSet { get; init; }
    /// <summary>
    /// The declared element count when the texture is an ARRAY of textures
    /// (<c>Texture2D Tex[N]</c>; <c>0</c> for an unbounded array), or <see langword="null"/> for a
    /// single texture. One texture declaration in HLSL, N resource slots in the shader. A
    /// SPIR-V reflection sets it from the <c>OpTypeArray</c> wrapping the image type, so a
    /// 1-element array is still reported; a DXIL/DXBC reflection sets it from the binding's
    /// <c>BindCount</c> when that is above 1 (issue #324).
    /// </summary>
    public int? ArrayLength { get; init; }
}
