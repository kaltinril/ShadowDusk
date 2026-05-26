#nullable enable

namespace ShadowDusk.Core.Reflection;

public sealed record ReflectedEffect
{
    public required IReadOnlyList<ConstantBufferReflection>      ConstantBuffers  { get; init; }
    public required IReadOnlyList<TextureReflection>             Textures         { get; init; }
    public required IReadOnlyList<SamplerReflection>             Samplers         { get; init; }
    public required IReadOnlyList<SignatureParameterReflection>  InputSignature   { get; init; }
    public required IReadOnlyList<SignatureParameterReflection>  OutputSignature  { get; init; }
    public required IReadOnlyList<ParameterReflection>           Parameters       { get; init; }
}
