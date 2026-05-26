#nullable enable

namespace ShadowDusk.Core.Reflection;

public sealed record ConstantBufferReflection
{
    public required string                        Name      { get; init; }
    public required int                           SizeBytes { get; init; }
    public required int                           BindSlot  { get; init; }
    public required IReadOnlyList<VariableReflection> Variables { get; init; }
}
