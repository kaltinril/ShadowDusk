#nullable enable

using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.HLSL.Reflection;

public sealed record ReflectionInput
{
    public required ReadOnlyMemory<byte>              DxilBlob      { get; init; }
    public ReadOnlyMemory<byte>                       SpirVBlob     { get; init; }
    public IReadOnlyList<ParameterAnnotation>?        FxAnnotations { get; init; }
}
