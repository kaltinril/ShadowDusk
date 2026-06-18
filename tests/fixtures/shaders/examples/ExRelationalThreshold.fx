// =============================================================================
// ExRelationalThreshold.fx  —  ShadowDusk fresh example fixture (issue #106)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #106).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the issue-#106 class — RELATIONAL operators used directly in
//              the pixel-shader body as scalar boolean expressions (not inside a
//              ternary, not inside clip()). Each of <, <=, >, >= contributes to a
//              banded threshold color so all four comparisons survive codegen.
// Exercises  : <, <=, >, >= in PS-body expressions; multiply-by-bool float math;
//              SpriteBatch PS shape.
// Regression : Before the FxPreParser fix, a bare relational operator in a shader
//              body was misparsed as an FX annotation, failing with FX0001.
// =============================================================================
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float LoEdge; // 0.25
float HiEdge; // 0.75

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR0
{
    float4 col = tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;

    float x = input.TextureCoordinates.x;
    float y = input.TextureCoordinates.y;

    // Each relational op produces a 0/1 scalar by bool->float promotion.
    float below   = x <  LoEdge;     // strictly-less
    float atOrLow = x <= LoEdge;     // less-or-equal
    float above   = y >  HiEdge;     // strictly-greater
    float atOrHi  = y >= HiEdge;     // greater-or-equal

    float mask = saturate(below + atOrLow * 0.5f + above + atOrHi * 0.5f);

    col.rgb *= 1.0f - mask * 0.5f;
    return col;
}

technique BasicColorDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
