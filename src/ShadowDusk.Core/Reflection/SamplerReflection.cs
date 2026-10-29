#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>A reflected sampler binding: its name, bind slot, and associated texture.</summary>
public sealed record SamplerReflection
{
    /// <summary>The sampler's name.</summary>
    public required string  Name        { get; init; }
    /// <summary>
    /// The sampler's bind slot. Same meaning per reflection as
    /// <see cref="TextureReflection.BindSlot"/>: the register from DXIL or DXBC, the position in
    /// binding order from the SPIR-V reflector, and <c>RawBinding - 32</c> on the Vulkan target.
    /// </summary>
    public required int     BindSlot    { get; init; }
    /// <summary>The name of the texture this sampler is paired with, if known.</summary>
    public string?          TextureName { get; init; }
    /// <summary>
    /// The raw SPIR-V <c>Binding</c> decoration (unlike <see cref="BindSlot"/>). Used by the
    /// Vulkan target, for the container's descriptor-layout table and to derive the slot.
    /// Default 0 for a DXIL or DXBC reflection.
    /// </summary>
    public int RawBinding { get; init; }
    /// <summary>The raw SPIR-V <c>DescriptorSet</c> decoration. See <see cref="RawBinding"/>.</summary>
    public int RawDescriptorSet { get; init; }
    /// <summary>
    /// True when this sampler and its texture are one combined SPIR-V resource
    /// (<c>OpTypeSampledImage</c>, from a legacy <c>sampler2D</c>-style declaration) rather
    /// than two separate resources (modern <c>Texture2D</c> + <c>SamplerState</c>). Used only
    /// by the Vulkan container's descriptor-layout table.
    /// </summary>
    public bool IsCombined { get; init; }
    /// <summary>
    /// The declared element count when the sampler is an ARRAY of samplers
    /// (<c>SamplerState S[N]</c>; <c>0</c> for an unbounded array), or <see langword="null"/>
    /// for a single sampler. See <see cref="TextureReflection.ArrayLength"/> (issue #324).
    /// </summary>
    public int? ArrayLength { get; init; }
}
