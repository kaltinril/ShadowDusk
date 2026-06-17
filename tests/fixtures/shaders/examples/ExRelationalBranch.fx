// =============================================================================
// ExRelationalBranch.fx  —  ShadowDusk fresh example fixture (issue #106)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #106).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the issue-#106 class along two more axes: (1) a relational
//              operator driving an if / else if / else branch in the PS body
//              (NOT inside clip()), and (2) a deeply NESTED / CHAINED ternary.
// Exercises  : if / else if / else with <, >, <=, && ; chained ?: (band-select);
//              SpriteBatch PS shape.
// Regression : Before the FxPreParser fix, the relational operators in the branch
//              conditions and the chained ternary were misparsed as FX annotations
//              and the compile failed with FX0001.
// =============================================================================
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float Threshold; // 0.5

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

    float lum = (col.r + col.g + col.b) / 3.0f;
    float x   = input.TextureCoordinates.x;

    // Relational-driven if / else if / else branch in the body (not clip()).
    if (lum < Threshold)
    {
        col.rgb *= 0.5f;
    }
    else if (lum > Threshold && x <= 0.5f)
    {
        col.rgb = saturate(col.rgb * 1.5f);
    }
    else
    {
        col.rgb = float3(lum, lum, lum);
    }

    // Nested / chained ternary over relationals (a 4-band select).
    float3 tint = x < 0.25f ? float3(1, 0, 0)
                : x < 0.50f ? float3(0, 1, 0)
                : x < 0.75f ? float3(0, 0, 1)
                :             float3(1, 1, 1);

    col.rgb = lerp(col.rgb, col.rgb * tint, 0.25f);
    return col;
}

technique BasicColorDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
