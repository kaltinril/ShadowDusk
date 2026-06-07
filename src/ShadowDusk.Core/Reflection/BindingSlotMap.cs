#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// Maps texture and sampler names to their bind slots, used to align reflected resource
/// names with the slots emitted in the compiled shader.
/// </summary>
public sealed record BindingSlotMap
{
    /// <summary>Texture name → bind slot.</summary>
    public required IReadOnlyDictionary<string, int> Textures { get; init; }
    /// <summary>Sampler name → bind slot.</summary>
    public required IReadOnlyDictionary<string, int> Samplers { get; init; }

    /// <summary>An empty map with no texture or sampler bindings.</summary>
    public static readonly BindingSlotMap Empty = new()
    {
        Textures = new Dictionary<string, int>(),
        Samplers = new Dictionary<string, int>(),
    };
}
