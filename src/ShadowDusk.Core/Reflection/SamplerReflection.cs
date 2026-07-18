#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>A reflected sampler binding: its name, bind slot, and associated texture.</summary>
public sealed record SamplerReflection
{
    /// <summary>The sampler's name.</summary>
    public required string  Name        { get; init; }
    /// <summary>The sampler's bind slot.</summary>
    public required int     BindSlot    { get; init; }
    /// <summary>The name of the texture this sampler is paired with, if known.</summary>
    public string?          TextureName { get; init; }
    /// <summary>
    /// The raw SPIR-V <c>Binding</c> decoration (unlike <see cref="BindSlot"/>). Used only
    /// by the Vulkan container's descriptor-layout table. Default 0 for non-Vulkan.
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
}
