#nullable enable

namespace ShadowDusk.Core.Reflection;

public sealed record ParameterReflection
{
    public required string               Name           { get; init; }
    public string?                       Semantic       { get; init; }
    public required EffectParameterClass Class          { get; init; }
    public required EffectParameterType  Type           { get; init; }
    public required int                  Rows           { get; init; }
    public required int                  Columns        { get; init; }
    public required int                  Elements       { get; init; }
    public IReadOnlyList<AnnotationReflection>? Annotations { get; init; }
}
