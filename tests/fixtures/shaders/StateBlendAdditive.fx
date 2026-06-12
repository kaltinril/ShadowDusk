// Phase 43 (F1) corpus fixture: in-pass BLEND states with explicit factors.
// PS-only SpriteBatch shape (the Phase 17 validated path), mgfxc-compilable for
// golden generation. Exercises AlphaBlendEnable + SrcBlend/DestBlend/BlendOp —
// including mgfxc's ToAlphaBlend derivation (SrcAlpha -> SourceAlpha on the alpha
// channel) and its BlendOp -> AlphaBlendFunction quirk, which ShadowDusk mirrors.
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

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

float4 MainPS(VertexShaderOutput input) : COLOR
{
    float4 col = tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
    return float4(col.rgb, 0.5);
}

technique BlendAdditive
{
    pass P0
    {
        AlphaBlendEnable = TRUE;
        SrcBlend         = SRCALPHA;
        DestBlend        = ONE;
        BlendOp          = ADD;
        PixelShader      = compile PS_SHADERMODEL MainPS();
    }
}
