#nullable enable

namespace ShadowDusk.Core.Reflection;

public sealed record AnnotationReflection
{
    public required string Name  { get; init; }
    public required string Value { get; init; }
}
