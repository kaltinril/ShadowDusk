// =============================================================================
// ExPhantomNonSquareMatrix.fx  —  ShadowDusk fresh regression fixture (#187)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #187).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the NON-SQUARE MATRIX member of the issue-#187 phantom class.
//              The only reads of `PhantomM` form an algebraic identity DXC's
//              -spirv backend cancels (fxc and the DXIL reflection companion do
//              not), so the OpenGL pipeline must SYNTHESIZE the parameter's
//              register backing — and must size it by the runtime's TRANSPOSED
//              matrix write model (MonoGame/KNI upload ColumnCount 16-byte
//              rows): a float2x4 needs FOUR registers, not two. An undersized
//              record crashes MonoGame with ArgumentException on the first
//              EffectPass.Apply, before any SetValue.
// Exercises  : float2x4 uniform whose reads fully fold away on the -spirv leg.
// Regression : Issue #187 (phantom-parameter backing, matrix footprint).
// =============================================================================
#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float2x4 PhantomM;

struct PixelInput
{
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

float4 MainPS(PixelInput input) : COLOR0
{
    // The #187 shape: (x * m) / m cancels to x under DXC's -spirv backend, so
    // the shipped GLSL never references PhantomM at all, while fxc — and the
    // DXIL companion compile reflection is sourced from — keeps both operations.
    float2 uv = (input.TexCoord * PhantomM[0].xy) / PhantomM[0].xy;
    return float4(uv, 0.0, 1.0);
}

technique PhantomMatrix
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
