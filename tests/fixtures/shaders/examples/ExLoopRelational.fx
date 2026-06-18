// =============================================================================
// ExLoopRelational.fx  —  ShadowDusk fresh example fixture (issue #106)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #106).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the issue-#106 class for a RELATIONAL operator in a for-loop
//              CONDITION (the corpus had no all-runtime SM3 loop fixture). A small
//              fixed-count horizontal tap accumulate, with a literal loop bound so
//              fxc/mgfxc can unroll it at ps_3_0 / ps_2_0 (D3D9 has no dynamic
//              loops in the pixel stage).
// Exercises  : for (int i = 0; i < N; i++) — relational loop condition; literal
//              array index reads (ArrayUniform.fx convention); accumulate-in-loop.
// Regression : Before the FxPreParser fix, the `<` in the loop condition was
//              misparsed as the start of an FX annotation and the compile failed
//              with FX0001.
// =============================================================================
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

#define TAP_COUNT 5

float TexelWidth; // horizontal spacing between taps, e.g. 1.0/textureWidth

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
    float4 sum = 0;

    // Relational condition in the loop header; literal-bounded so it unrolls.
    for (int i = 0; i < TAP_COUNT; i++)
    {
        float offset = (i - (TAP_COUNT - 1) * 0.5f) * TexelWidth;
        float2 uv = input.TextureCoordinates + float2(offset, 0.0f);
        sum += tex2D(SpriteTextureSampler, uv);
    }

    return (sum / TAP_COUNT) * input.Color;
}

technique BasicColorDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
