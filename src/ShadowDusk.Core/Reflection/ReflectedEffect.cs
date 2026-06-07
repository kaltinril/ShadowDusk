#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// The complete reflected metadata for one compiled shader: its constant buffers, texture
/// and sampler bindings, input/output signatures, and flattened effect parameters. Produced
/// by <see cref="IShaderReflector"/> and consumed when writing the <c>.mgfx</c> container.
/// </summary>
public sealed record ReflectedEffect
{
    /// <summary>The constant (uniform) buffers declared by the shader.</summary>
    public required IReadOnlyList<ConstantBufferReflection>      ConstantBuffers  { get; init; }
    /// <summary>The texture resources bound by the shader.</summary>
    public required IReadOnlyList<TextureReflection>             Textures         { get; init; }
    /// <summary>The samplers bound by the shader.</summary>
    public required IReadOnlyList<SamplerReflection>             Samplers         { get; init; }
    /// <summary>The shader's input signature (stage input attributes).</summary>
    public required IReadOnlyList<SignatureParameterReflection>  InputSignature   { get; init; }
    /// <summary>The shader's output signature (stage output attributes).</summary>
    public required IReadOnlyList<SignatureParameterReflection>  OutputSignature  { get; init; }
    /// <summary>The flattened effect parameters exposed to the MonoGame runtime.</summary>
    public required IReadOnlyList<ParameterReflection>           Parameters       { get; init; }
}
