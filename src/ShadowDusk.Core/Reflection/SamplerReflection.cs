#nullable enable

namespace ShadowDusk.Core.Reflection;

public sealed record SamplerReflection
{
    public required string  Name        { get; init; }
    public required int     BindSlot    { get; init; }
    public string?          TextureName { get; init; }
}
