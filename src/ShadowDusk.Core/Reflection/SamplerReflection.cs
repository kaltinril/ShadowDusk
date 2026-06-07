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
}
