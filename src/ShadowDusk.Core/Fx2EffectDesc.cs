#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// The input IR for <see cref="Fx2EffectWriter"/> — everything needed to author a D3D9
/// Effects Framework binary ("fx_2_0", token 0xFEFF0901) for FNA. This is deliberately a
/// separate shape from <see cref="ShaderIR"/>: the fx_2_0 container carries typed effect
/// parameters with default values and sampler-state blocks that MGFX has no analog for.
/// Field encodings follow <c>docs/fx2-binary-format.md</c> (MojoShader's parser is the spec).
/// </summary>
public sealed record Fx2EffectDesc
{
    /// <summary>
    /// The effect parameters, in emission order. Per FNA's loader, every texture parameter
    /// MUST precede any sampler parameter that references it, and for every CTAB constant in
    /// every shader there MUST be a parameter with the identical name.
    /// </summary>
    public required IReadOnlyList<Fx2Parameter> Parameters { get; init; }

    /// <summary>The techniques, in source order. MojoShader requires at least one.</summary>
    public required IReadOnlyList<Fx2Technique> Techniques { get; init; }

    /// <summary>
    /// The compiled SM1–3 shader blobs; <see cref="Fx2Pass.VertexShaderIndex"/> /
    /// <see cref="Fx2Pass.PixelShaderIndex"/> index into this list.
    /// </summary>
    public required IReadOnlyList<Fx2Shader> Shaders { get; init; }
}

/// <summary>
/// One fx_2_0 effect parameter. Three kinds share this record, distinguished by
/// <see cref="Class"/>/<see cref="Type"/> (raw D3DXPARAMETER / MojoShader symbol values,
/// see <c>docs/fx2-binary-format.md</c> §6.1): numerics (class 0–3, type bool/int/float),
/// textures (class 4, type 5–9), and samplers (class 4, type 10–14, carrying
/// <see cref="SamplerStates"/>).
/// </summary>
public sealed record Fx2Parameter
{
    public required string Name { get; init; }

    /// <summary>Optional HLSL semantic; <see langword="null"/> = none.</summary>
    public string? Semantic { get; init; }

    /// <summary>D3DXPARAMETER_CLASS: 0=scalar, 1=vector, 2=matrix-rows, 3=matrix-cols, 4=object.</summary>
    public required int Class { get; init; }

    /// <summary>D3DXPARAMETER_TYPE: 1=bool, 2=int, 3=float, 5=texture, 10–14=sampler…</summary>
    public required int Type { get; init; }

    /// <summary>Logical row count (numeric classes only).</summary>
    public int Rows { get; init; }

    /// <summary>Logical column count (numeric classes only).</summary>
    public int Columns { get; init; }

    /// <summary>Array element count; 0 = not an array (0 and 1 are distinct on disk).</summary>
    public int Elements { get; init; }

    /// <summary>
    /// Default value for numeric parameters, laid out as the file rows of the value blob
    /// (row r = the contents of constant register r — <c>Rows × Columns</c> floats per
    /// element). <see langword="null"/> writes zeros, matching the MGFX writer's behavior.
    /// </summary>
    public IReadOnlyList<float>? DefaultValue { get; init; }

    /// <summary>Sampler-state assignments (sampler parameters only).</summary>
    public IReadOnlyList<Fx2SamplerState> SamplerStates { get; init; } = [];
}

/// <summary>
/// One sampler-state assignment inside a sampler parameter's value blob. Exactly one of
/// <see cref="TextureParameterName"/> (op 164/Texture), <see cref="FloatValue"/>, or
/// <see cref="IntValue"/> is set.
/// </summary>
public sealed record Fx2SamplerState
{
    /// <summary>
    /// The on-disk state op — 164-based (<c>0xA4</c> = Texture … <c>0xAE</c> = MaxAnisotropy).
    /// The writer rejects the four ops FNA's runtime throws on
    /// (BorderColor 168, SRGBTexture 175, ElementIndex 176, DMapOffset 177).
    /// </summary>
    public required int Operation { get; init; }

    /// <summary>For op 164 (Texture): the name of the texture parameter to bind.</summary>
    public string? TextureParameterName { get; init; }

    /// <summary>Float-valued states (MipMapLodBias).</summary>
    public float? FloatValue { get; init; }

    /// <summary>Integer/enum-valued states (filters, address modes, …).</summary>
    public int? IntValue { get; init; }
}

/// <summary>One fx_2_0 technique.</summary>
public sealed record Fx2Technique(string Name, IReadOnlyList<Fx2Pass> Passes);

/// <summary>
/// One fx_2_0 pass. Shader indices of <c>-1</c> mean the stage is absent — the writer then
/// omits the VertexShader/PixelShader state entirely (MojoShader keeps the previously bound
/// shader; emitting a "NULL shader" state is the unresolved F4 ambiguity).
/// </summary>
public sealed record Fx2Pass(
    string Name,
    int VertexShaderIndex,
    int PixelShaderIndex,
    IReadOnlyList<Fx2RenderState> RenderStates);

/// <summary>
/// One pass render-state assignment. <see cref="Operation"/> is the MojoShader
/// renderStateType file value (NOT a D3DRS number); <see cref="Value"/> carries the raw
/// dword (D3D9-domain enum value, 0/1 bool, or IEEE-754 bits when <see cref="IsFloat"/>).
/// The writer restricts ops to the set FNA's runtime honors — anything else makes FNA throw
/// at load time.
/// </summary>
public sealed record Fx2RenderState(int Operation, uint Value, bool IsFloat = false);

/// <summary>One compiled SM1–3 shader blob (bare D3D9 token stream with CTAB).</summary>
public sealed record Fx2Shader(ShaderStage Stage, ReadOnlyMemory<byte> Bytecode);
