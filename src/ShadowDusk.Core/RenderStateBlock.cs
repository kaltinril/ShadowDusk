#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// The render states declared inside a single FX pass, parsed from the source by
/// <see cref="RenderStateParser"/>. Every state is nullable so a state left unset in the
/// source stays absent (rather than defaulting), which lets the <see cref="MgfxWriter"/>
/// decide via the <c>Has*</c> gates whether to emit each of the three optional MGFX
/// state-object headers. The FNA-only states (and <see cref="KnownFnaThrowingStates"/>)
/// are consumed solely by the FNA fx_2_0 path and are deliberately kept out of the
/// <c>Has*</c> gates so they never alter MGFX v10 output.
/// </summary>
public sealed record RenderStateBlock
{
    // Rasterizer

    /// <summary>Which triangle faces are culled.</summary>
    public CullModeValue?        CullMode             { get; init; }

    /// <summary>Whether triangles are filled solid or drawn as wireframe.</summary>
    public FillModeValue?        FillMode             { get; init; }

    /// <summary>Whether the scissor-rectangle test is enabled.</summary>
    public bool?                 ScissorTestEnable    { get; init; }

    /// <summary>Whether multisample antialiasing is enabled.</summary>
    public bool?                 MultiSampleAntiAlias { get; init; }

    /// <summary>Constant depth bias added to a fragment's depth.</summary>
    public float?                DepthBias            { get; init; }

    /// <summary>Slope-scaled depth bias factor.</summary>
    public float?                SlopeScaleDepthBias  { get; init; }

    // Blend

    /// <summary>Whether alpha blending is enabled for this pass.</summary>
    public bool?               AlphaBlendEnable        { get; init; }

    /// <summary>Source blend factor for the color channels.</summary>
    public BlendValue?         ColorSourceBlend        { get; init; }

    /// <summary>Destination blend factor for the color channels.</summary>
    public BlendValue?         ColorDestinationBlend   { get; init; }

    /// <summary>Blend function combining the color source and destination terms.</summary>
    public BlendFunctionValue? ColorBlendFunction      { get; init; }

    /// <summary>Source blend factor for the alpha channel.</summary>
    public BlendValue?         AlphaSourceBlend        { get; init; }

    /// <summary>Destination blend factor for the alpha channel.</summary>
    public BlendValue?         AlphaDestinationBlend   { get; init; }

    /// <summary>Blend function combining the alpha source and destination terms.</summary>
    public BlendFunctionValue? AlphaBlendFunction      { get; init; }

    /// <summary>Color-channel write mask (a <c>D3DCOLORWRITEENABLE</c> bitmask: Red|Green|Blue|Alpha).</summary>
    public int?                ColorWriteChannels      { get; init; }

    // Depth/Stencil

    /// <summary>Whether the depth (z) buffer test is enabled.</summary>
    public bool?                  DepthBufferEnable      { get; init; }

    /// <summary>Whether depth writes to the z-buffer are enabled.</summary>
    public bool?                  DepthBufferWriteEnable { get; init; }

    /// <summary>Comparison function for the depth test.</summary>
    public CompareFunctionValue?  DepthBufferFunction    { get; init; }

    /// <summary>Whether stencil testing is enabled.</summary>
    public bool?                  StencilEnable          { get; init; }

    /// <summary>Reference value the stencil test compares against.</summary>
    public int?                   ReferenceStencil       { get; init; }

    /// <summary>Mask applied to both the reference and stored stencil values before comparison.</summary>
    public int?                   StencilMask            { get; init; }

    /// <summary>Mask controlling which stencil bits may be written.</summary>
    public int?                   StencilWriteMask       { get; init; }

    /// <summary>Stencil operation when the stencil test fails.</summary>
    public StencilOperationValue? StencilFail            { get; init; }

    /// <summary>Stencil operation when the stencil test passes but the depth test fails.</summary>
    public StencilOperationValue? StencilDepthBufferFail { get; init; }

    /// <summary>Stencil operation when both the stencil and depth tests pass.</summary>
    public StencilOperationValue? StencilPass            { get; init; }

    /// <summary>Comparison function for the stencil test.</summary>
    public CompareFunctionValue?  StencilFunction        { get; init; }

    // FNA-only states (fx_2_0 ops FNA's Effect runtime honors but MGFX has no analog
    // for). These are INTENTIONALLY excluded from the Has* gates below: MgfxWriter keys
    // its three optional state-object headers off Has*, so including them would change
    // MGFX v10 output for sources that set only these keys. Only Fx2EffectBuilder
    // (the FNA path) consumes them.

    // Blend (FNA-only)

    /// <summary>FNA fx_2_0 only (op 99): enable separate color/alpha blend functions.</summary>
    public bool? SeparateAlphaBlendEnable { get; init; }              // op 99

    /// <summary>FNA fx_2_0 only (op 96): constant blend factor, as a D3DCOLOR dword.</summary>
    public uint? BlendFactor              { get; init; }              // op 96, D3DCOLOR dword

    /// <summary>FNA fx_2_0 only (op 93): color-write mask for render target 1.</summary>
    public int?  ColorWriteChannels1      { get; init; }              // op 93

    /// <summary>FNA fx_2_0 only (op 94): color-write mask for render target 2.</summary>
    public int?  ColorWriteChannels2     { get; init; }               // op 94

    /// <summary>FNA fx_2_0 only (op 95): color-write mask for render target 3.</summary>
    public int?  ColorWriteChannels3     { get; init; }               // op 95

    // Depth/Stencil (FNA-only, two-sided / counter-clockwise face set)

    /// <summary>FNA fx_2_0 only (op 88): enable two-sided (front/back) stencil operations.</summary>
    public bool?                  TwoSidedStencilMode                    { get; init; } // op 88

    /// <summary>FNA fx_2_0 only (op 89): back-face stencil operation when the stencil test fails.</summary>
    public StencilOperationValue? CounterClockwiseStencilFail            { get; init; } // op 89

    /// <summary>FNA fx_2_0 only (op 90): back-face stencil operation when stencil passes but depth fails.</summary>
    public StencilOperationValue? CounterClockwiseStencilDepthBufferFail { get; init; } // op 90

    /// <summary>FNA fx_2_0 only (op 91): back-face stencil operation when both tests pass.</summary>
    public StencilOperationValue? CounterClockwiseStencilPass            { get; init; } // op 91

    /// <summary>FNA fx_2_0 only (op 92): back-face stencil comparison function.</summary>
    public CompareFunctionValue?  CounterClockwiseStencilFunction        { get; init; } // op 92

    // Rasterizer (FNA-only)

    /// <summary>FNA fx_2_0 only (op 68): multisample coverage mask, as a dword.</summary>
    public uint? MultiSampleMask { get; init; }                       // op 68, dword mask

    /// <summary>
    /// FX render-state keys present in the pass that FNA's Effect runtime throws
    /// <c>NotImplementedException</c> on at <c>EffectPass.Apply</c> (the non-honored ops
    /// of docs/fx2-binary-format.md §8.2, e.g. AlphaTestEnable, fog and point-sprite
    /// states). Recorded by <see cref="RenderStateParser"/> so the FNA path can fail
    /// loudly instead of silently diverging from the fxc build (which crashes FNA at
    /// runtime). Sorted ordinally for deterministic diagnostics. MGFX paths ignore this
    /// metadata entirely.
    /// </summary>
    public IReadOnlyList<string> KnownFnaThrowingStates { get; init; } = [];

    /// <summary>
    /// Whether any MGFX-honored blend state is set, gating whether the
    /// <see cref="MgfxWriter"/> emits the optional blend-state header. (The FNA-only blend
    /// states are deliberately excluded so they never change MGFX v10 output.)
    /// </summary>
    public bool HasBlendState =>
        AlphaBlendEnable.HasValue || ColorSourceBlend.HasValue || ColorDestinationBlend.HasValue ||
        ColorBlendFunction.HasValue || AlphaSourceBlend.HasValue || AlphaDestinationBlend.HasValue ||
        AlphaBlendFunction.HasValue || ColorWriteChannels.HasValue;

    /// <summary>
    /// Whether any MGFX-honored depth/stencil state is set, gating whether the
    /// <see cref="MgfxWriter"/> emits the optional depth-stencil-state header. (The FNA-only
    /// two-sided stencil states are deliberately excluded.)
    /// </summary>
    public bool HasDepthStencilState =>
        DepthBufferEnable.HasValue || DepthBufferWriteEnable.HasValue || DepthBufferFunction.HasValue ||
        StencilEnable.HasValue || ReferenceStencil.HasValue || StencilMask.HasValue ||
        StencilWriteMask.HasValue || StencilFail.HasValue || StencilDepthBufferFail.HasValue ||
        StencilPass.HasValue || StencilFunction.HasValue;

    /// <summary>
    /// Whether any MGFX-honored rasterizer state is set, gating whether the
    /// <see cref="MgfxWriter"/> emits the optional rasterizer-state header. (The FNA-only
    /// multisample mask is deliberately excluded.)
    /// </summary>
    public bool HasRasterizerState =>
        CullMode.HasValue || FillMode.HasValue || ScissorTestEnable.HasValue ||
        MultiSampleAntiAlias.HasValue || DepthBias.HasValue || SlopeScaleDepthBias.HasValue;
}

// These mirror MonoGame 3.8.2 enum ordinal values, verified field-by-field against
// the v3.8.2 tag's MonoGame.Framework/Graphics/States/{CullMode,FillMode,Blend,
// BlendFunction,CompareFunction,StencilOperation}.cs (Phase 43, F1b). The MGFX
// writer serializes these ordinals as single bytes that MonoGame's Effect reader
// casts straight back to its enums, so they MUST be MonoGame's values — NOT the
// D3D9 ones (the FNA path maps symbolically to D3D9 in Fx2EffectBuilder instead).
/// <summary>Which triangle faces are culled (mirrors MonoGame's <c>CullMode</c> ordinals).</summary>
public enum CullModeValue : int
{
    /// <summary>Cull nothing; draw both faces.</summary>
    None                    = 0,
    /// <summary>Cull clockwise-wound (front) faces.</summary>
    CullClockwiseFace       = 1,
    /// <summary>Cull counter-clockwise-wound (back) faces.</summary>
    CullCounterClockwiseFace = 2,
}

/// <summary>How triangles are rasterized (mirrors MonoGame's <c>FillMode</c> ordinals).</summary>
public enum FillModeValue : int
{
    /// <summary>Fill triangle interiors.</summary>
    Solid     = 0,
    /// <summary>Draw triangle edges only.</summary>
    WireFrame = 1,
}

/// <summary>A blend factor applied to a source or destination color (mirrors MonoGame's <c>Blend</c> ordinals).</summary>
public enum BlendValue : int
{
    /// <summary>Factor of (1, 1, 1, 1).</summary>
    One                    = 0,
    /// <summary>Factor of (0, 0, 0, 0).</summary>
    Zero                   = 1,
    /// <summary>Factor of the source color.</summary>
    SourceColor            = 2,
    /// <summary>Factor of one minus the source color.</summary>
    InverseSourceColor     = 3,
    /// <summary>Factor of the source alpha.</summary>
    SourceAlpha            = 4,
    /// <summary>Factor of one minus the source alpha.</summary>
    InverseSourceAlpha     = 5,
    /// <summary>Factor of the destination color.</summary>
    DestinationColor       = 6,
    /// <summary>Factor of one minus the destination color.</summary>
    InverseDestinationColor = 7,
    /// <summary>Factor of the destination alpha.</summary>
    DestinationAlpha       = 8,
    /// <summary>Factor of one minus the destination alpha.</summary>
    InverseDestinationAlpha = 9,
    /// <summary>Factor of the constant blend factor.</summary>
    BlendFactor            = 10,
    /// <summary>Factor of one minus the constant blend factor.</summary>
    InverseBlendFactor     = 11,
    /// <summary>Factor of the source alpha, saturated to [0, 1].</summary>
    SourceAlphaSaturation  = 12,
}

/// <summary>How source and destination blend terms are combined (mirrors MonoGame's <c>BlendFunction</c> ordinals).</summary>
public enum BlendFunctionValue : int
{
    /// <summary>Add the source and destination terms.</summary>
    Add            = 0,
    /// <summary>Subtract the destination term from the source term.</summary>
    Subtract       = 1,
    /// <summary>Subtract the source term from the destination term.</summary>
    ReverseSubtract = 2,
    /// <summary>Take the component-wise minimum of the two terms.</summary>
    Min            = 3,
    /// <summary>Take the component-wise maximum of the two terms.</summary>
    Max            = 4,
}

/// <summary>A depth- or stencil-test comparison function (mirrors MonoGame's <c>CompareFunction</c> ordinals).</summary>
public enum CompareFunctionValue : int
{
    /// <summary>The test always passes.</summary>
    Always       = 0,
    /// <summary>The test never passes.</summary>
    Never        = 1,
    /// <summary>Pass when the new value is less than the stored value.</summary>
    Less         = 2,
    /// <summary>Pass when the new value is less than or equal to the stored value.</summary>
    LessEqual    = 3,
    /// <summary>Pass when the new value equals the stored value.</summary>
    Equal        = 4,
    /// <summary>Pass when the new value is greater than or equal to the stored value.</summary>
    GreaterEqual = 5,
    /// <summary>Pass when the new value is greater than the stored value.</summary>
    Greater      = 6,
    /// <summary>Pass when the new value does not equal the stored value.</summary>
    NotEqual     = 7,
}

/// <summary>What happens to a stencil value on a test outcome (mirrors MonoGame's <c>StencilOperation</c> ordinals).</summary>
public enum StencilOperationValue : int
{
    /// <summary>Keep the existing stencil value.</summary>
    Keep                = 0,
    /// <summary>Set the stencil value to zero.</summary>
    Zero                = 1,
    /// <summary>Replace the stencil value with the reference value.</summary>
    Replace             = 2,
    /// <summary>Increment the stencil value, wrapping on overflow.</summary>
    Increment           = 3,
    /// <summary>Decrement the stencil value, wrapping on underflow.</summary>
    Decrement           = 4,
    /// <summary>Increment the stencil value, clamping at the maximum.</summary>
    IncrementSaturation = 5,
    /// <summary>Decrement the stencil value, clamping at zero.</summary>
    DecrementSaturation = 6,
    /// <summary>Bitwise-invert the stencil value.</summary>
    Invert              = 7,
}
