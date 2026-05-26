#nullable enable

namespace ShadowDusk.Core.Reflection;

public sealed record BindingSlotMap
{
    public required IReadOnlyDictionary<string, int> Textures { get; init; }
    public required IReadOnlyDictionary<string, int> Samplers { get; init; }

    public static readonly BindingSlotMap Empty = new()
    {
        Textures = new Dictionary<string, int>(),
        Samplers = new Dictionary<string, int>(),
    };
}
