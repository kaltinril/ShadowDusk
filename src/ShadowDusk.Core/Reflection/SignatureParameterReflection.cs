#nullable enable

namespace ShadowDusk.Core.Reflection;

public sealed record SignatureParameterReflection
{
    public required string SemanticName  { get; init; }
    public required int    SemanticIndex { get; init; }
    public required int    Register      { get; init; }
    public required string SystemValue   { get; init; }
    public required string ComponentType { get; init; }
    public required byte   Mask          { get; init; }
}
