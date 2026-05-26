#nullable enable

namespace ShadowDusk.Core.Reflection;

public sealed record TextureReflection
{
    public required string           Name      { get; init; }
    public required int              BindSlot  { get; init; }
    public required TextureDimension Dimension { get; init; }
}
