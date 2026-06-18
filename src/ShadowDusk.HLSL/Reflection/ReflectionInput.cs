#nullable enable

using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.HLSL.Reflection;

/// <summary>
/// The input to the DXIL reflection pipeline: the compiled DXIL blob to reflect, plus the
/// FX parameter annotations recovered by the pre-parser (which DXIL itself does not carry)
/// so the reflected parameters can be re-associated with their source annotations.
/// </summary>
public sealed record ReflectionInput
{
    /// <summary>The compiled DXIL blob to reflect.</summary>
    public required ReadOnlyMemory<byte>              DxilBlob      { get; init; }

    /// <summary>
    /// Per-parameter FX annotations from the pre-parser, matched back onto the reflected
    /// parameters by name; <see langword="null"/> when the source declared no annotations.
    /// </summary>
    public IReadOnlyList<ParameterAnnotation>?        FxAnnotations { get; init; }
}
