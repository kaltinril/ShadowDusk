#nullable enable

namespace ShadowDusk.HLSL.Ast;

/// <summary>The output of the FX9 pre-parser: stripped HLSL plus all extracted FX9 metadata.</summary>
public sealed record FxParseResult
{
    /// <summary>HLSL source with all FX9 blocks stripped, preserving line numbers for error reporting.</summary>
    public required string StrippedHlsl { get; init; }

    /// <summary>All technique blocks extracted from the source.</summary>
    public required IReadOnlyList<TechniqueInfo> Techniques { get; init; }

    /// <summary>All sampler declarations with sampler_state blocks extracted from the source.</summary>
    public required IReadOnlyList<SamplerInfo> Samplers { get; init; }

    /// <summary>Annotation blocks attached to global parameter declarations.</summary>
    public required IReadOnlyList<ParameterAnnotation> ParameterAnnotations { get; init; }

    /// <summary>
    /// TEXTURE name -> the OpenGL texture unit an explicit <c>register(sN)</c> on its LEGACY
    /// sampler declaration pins it to. Keyed on the texture (synthesized <c>X_SDTexture</c>, or
    /// the one a <c>sampler_state</c> block references) because that is what the GL sampler
    /// table joins on. Empty when nothing declared a register.
    ///
    /// <para>This exists because the register clause is otherwise <b>destroyed before DXC sees
    /// it</b>: the SM4 rewrite turns <c>sampler X : register(s2);</c> into
    /// <c>Texture2D X_SDTexture; SamplerState X;</c>, so neither reflection nor the SPIR-V can
    /// recover the 2. The clause is recorded here rather than preserved in the rewritten HLSL
    /// deliberately: emitting it would change what DXC compiles and move the DirectX, DX12,
    /// Vulkan and FNA bytes, none of which have a reported defect. Recording it changes the
    /// OpenGL slot allocation only.</para>
    ///
    /// <para><b>Legacy form only, and that is measured, not an oversight</b> (2026-08-02).
    /// <c>mgfxc</c> honours the annotation exactly here, because compiled at <c>ps_3_0</c> a
    /// legacy <c>sampler</c> IS the combined sampler and <c>register(sN)</c> pins its SM3
    /// sampler register. For the modern spelling it does not: given
    /// <c>Texture2D T : register(t3); SamplerState S : register(s2);</c> the <c>mgfxc</c>
    /// OpenGL build puts the pair on slot 0 regardless, allocating by texture declaration
    /// order. Recording modern registers here would therefore make us DIVERGE.</para>
    /// </summary>
    public IReadOnlyDictionary<string, int> ExplicitGlSamplerSlots { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}
