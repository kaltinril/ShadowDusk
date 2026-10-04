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
    ///
    /// <para><b>This is the reading of whatever text the parser was given</b>, which for a compile
    /// is the raw source: a register that only exists in an inactive <c>#if</c> branch is counted
    /// and one spelled through a macro is missed (issue #299). The OpenGL target and the raylib
    /// converter therefore take this map from
    /// <see cref="FxPreParser.CollectGlSamplerSlots(string, string, FxParseResult)"/>, which
    /// reads it off the preprocessed view the way <c>mgfxc</c> does.</para>
    /// </summary>
    public IReadOnlyDictionary<string, int> ExplicitGlSamplerSlots { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// SAMPLER name -> the texture the SM4 rewrite made it sample through (the one its
    /// <c>sampler_state</c> block references, or the synthesized <c>X_SDTexture</c>). Only
    /// samplers a legacy intrinsic (<c>tex2D</c> …) reads have an entry. This is the join
    /// <see cref="FxPreParser.CollectGlSamplerSlots(string, string, FxParseResult)"/> needs to turn
    /// a sampler's preprocessed <c>register(sN)</c> into the texture-keyed map the OpenGL sampler
    /// table joins on: it has to be the REAL rewrite's binding, because that is the texture name
    /// the compiled SPIR-V carries.
    /// </summary>
    internal IReadOnlyDictionary<string, string> LegacySamplerTextures { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// SAMPLER name -> the <c>Texture2D</c> the SM4 rewrite SYNTHESIZED for it, for every legacy
    /// sampler that binds no texture of its own (<c>sampler2D A;</c>, <c>sampler A : register(s1);</c>,
    /// <c>sampler2D A = sampler_state { MinFilter = Point; };</c>). The rewrite has to give the
    /// texture a name of its own (<c>A_SDTexture</c>), because HLSL cannot declare a
    /// <c>Texture2D A</c> beside the <c>SamplerState A</c>; but to <c>mgfxc</c> the legacy
    /// sampler is ONE object, and the effect parameter it emits for it is named <c>A</c> on every
    /// profile. The compiler uses this map to give the parameter that name back, so
    /// <c>effect.Parameters["A"]</c> finds the texture exactly as it does under <c>mgfxc</c>.
    /// Empty when nothing was synthesized (including the FNA <c>fx_2_0</c> mode, which keeps
    /// the legacy declaration verbatim).
    /// </summary>
    public IReadOnlyDictionary<string, string> SynthesizedSamplerTextures { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// SAMPLER name -> the texture its <c>sampler_state</c> block names (<c>Texture = &lt;T&gt;</c>),
    /// for every legacy sampler the rewrite turned into <c>SamplerState</c> whose texture the MAIN
    /// file's code never declares or mentions. <c>mgfxc</c> reads the name off the state block and
    /// emits a <c>T</c> parameter even when nothing declares <c>T</c>, while the rewritten
    /// <c>T.Sample(...)</c> needs a declaration. These are CANDIDATES: an <c>#include</c>d file or a
    /// macro can still declare <c>T</c>, so the compiler adds <c>Texture2D T;</c> only when the
    /// preprocessed source (includes inlined, macros expanded) declares no <c>T</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> UndeclaredStateTextures { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// OpenGL sampler registers that an explicit <c>register(sN)</c> on a MODERN
    /// <c>SamplerState</c> declaration takes out of circulation, so a synthesized combined
    /// sampler must be allocated around them rather than onto them.
    ///
    /// <para>Compiling for OpenGL means compiling at <c>ps_3_0</c>, where a texture and a sampler
    /// are ONE object sharing a single register namespace. A modern <c>SamplerState</c> still sits
    /// at its declared register, but the combined sampler fxc synthesizes for each
    /// (texture, sampler) pair cannot reuse it. Measured: with one texture and
    /// <c>SamplerState S : register(s0)</c>, <c>mgfxc</c> emits <c>ps_s1</c>, not
    /// <c>ps_s0</c>.</para>
    ///
    /// <para>This is what makes the LEGACY case stop looking like a special case. There the
    /// sampler <i>is</i> the combined object, so it both reserves its register and occupies it,
    /// landing the pair exactly on <c>N</c>. Same allocator, different starting facts.</para>
    /// </summary>
    public IReadOnlySet<int> ReservedGlSamplerSlots { get; init; } = new HashSet<int>();
}
