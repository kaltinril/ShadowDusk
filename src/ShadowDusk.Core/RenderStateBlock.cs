#nullable enable

namespace ShadowDusk.Core;

public sealed record RenderStateBlock
{
    // Rasterizer
    public CullModeValue?        CullMode             { get; init; }
    public FillModeValue?        FillMode             { get; init; }
    public bool?                 ScissorTestEnable    { get; init; }
    public bool?                 MultiSampleAntiAlias { get; init; }
    public float?                DepthBias            { get; init; }
    public float?                SlopeScaleDepthBias  { get; init; }

    // Blend
    public bool?               AlphaBlendEnable        { get; init; }
    public BlendValue?         ColorSourceBlend        { get; init; }
    public BlendValue?         ColorDestinationBlend   { get; init; }
    public BlendFunctionValue? ColorBlendFunction      { get; init; }
    public BlendValue?         AlphaSourceBlend        { get; init; }
    public BlendValue?         AlphaDestinationBlend   { get; init; }
    public BlendFunctionValue? AlphaBlendFunction      { get; init; }
    public int?                ColorWriteChannels      { get; init; }

    // Depth/Stencil
    public bool?                  DepthBufferEnable      { get; init; }
    public bool?                  DepthBufferWriteEnable { get; init; }
    public CompareFunctionValue?  DepthBufferFunction    { get; init; }
    public bool?                  StencilEnable          { get; init; }
    public int?                   ReferenceStencil       { get; init; }
    public int?                   StencilMask            { get; init; }
    public int?                   StencilWriteMask       { get; init; }
    public StencilOperationValue? StencilFail            { get; init; }
    public StencilOperationValue? StencilDepthBufferFail { get; init; }
    public StencilOperationValue? StencilPass            { get; init; }
    public CompareFunctionValue?  StencilFunction        { get; init; }

    public bool HasBlendState =>
        AlphaBlendEnable.HasValue || ColorSourceBlend.HasValue || ColorDestinationBlend.HasValue ||
        ColorBlendFunction.HasValue || AlphaSourceBlend.HasValue || AlphaDestinationBlend.HasValue ||
        AlphaBlendFunction.HasValue || ColorWriteChannels.HasValue;

    public bool HasDepthStencilState =>
        DepthBufferEnable.HasValue || DepthBufferWriteEnable.HasValue || DepthBufferFunction.HasValue ||
        StencilEnable.HasValue || ReferenceStencil.HasValue || StencilMask.HasValue ||
        StencilWriteMask.HasValue || StencilFail.HasValue || StencilDepthBufferFail.HasValue ||
        StencilPass.HasValue || StencilFunction.HasValue;

    public bool HasRasterizerState =>
        CullMode.HasValue || FillMode.HasValue || ScissorTestEnable.HasValue ||
        MultiSampleAntiAlias.HasValue || DepthBias.HasValue || SlopeScaleDepthBias.HasValue;
}

// These mirror MonoGame 3.8.2 enum ordinal values (verified against MonoGame source).
// CullMode mirrors D3D9: None=1, not 0.
public enum CullModeValue : int
{
    None                    = 1,
    CullClockwiseFace       = 2,
    CullCounterClockwiseFace = 3,
}

public enum FillModeValue : int
{
    Solid     = 0,
    WireFrame = 1,
}

public enum BlendValue : int
{
    One                    = 1,
    Zero                   = 0,
    SourceColor            = 2,
    InverseSourceColor     = 3,
    SourceAlpha            = 4,
    InverseSourceAlpha     = 5,
    DestinationAlpha       = 6,
    InverseDestinationAlpha = 7,
    DestinationColor       = 8,
    InverseDestinationColor = 9,
    SourceAlphaSaturation  = 10,
    BlendFactor            = 11,
    InverseBlendFactor     = 12,
}

public enum BlendFunctionValue : int
{
    Add            = 0,
    Subtract       = 1,
    ReverseSubtract = 2,
    Min            = 3,
    Max            = 4,
}

public enum CompareFunctionValue : int
{
    Always       = 0,
    Never        = 1,
    Less         = 2,
    LessEqual    = 3,
    Equal        = 4,
    GreaterEqual = 5,
    Greater      = 6,
    NotEqual     = 7,
}

public enum StencilOperationValue : int
{
    Keep                = 0,
    Zero                = 1,
    Replace             = 2,
    Increment           = 3,
    Decrement           = 4,
    IncrementSaturation = 5,
    DecrementSaturation = 6,
    Invert              = 7,
}
