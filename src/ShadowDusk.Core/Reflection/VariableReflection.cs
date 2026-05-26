#nullable enable

namespace ShadowDusk.Core.Reflection;

public sealed record VariableReflection
{
    public required string               Name           { get; init; }
    public required int                  StartOffset    { get; init; }
    public required int                  SizeBytes      { get; init; }
    public required EffectParameterClass ParameterClass { get; init; }
    public required EffectParameterType  ParameterType  { get; init; }
    public required int                  Rows           { get; init; }
    public required int                  Columns        { get; init; }
    public required int                  Elements       { get; init; }
    public IReadOnlyList<VariableReflection>? Members   { get; init; }
}
