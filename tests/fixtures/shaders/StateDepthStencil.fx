// Phase 43 (F1) corpus fixture: in-pass DEPTH + STENCIL states across the full
// fixed-layout field set. PS-only SpriteBatch shape, mgfxc-compilable for golden
// generation (keys spelled per mgfxc 3.8.2's MGFX.tpg: ZEnable/ZWriteEnable/ZFunc,
// StencilFunc/StencilZFail/StencilRef).
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
    return tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
}

technique DepthStencil
{
    pass P0
    {
        ZEnable          = TRUE;
        ZWriteEnable     = FALSE;
        ZFunc            = ALWAYS;
        StencilEnable    = TRUE;
        StencilFunc      = EQUAL;
        StencilPass      = INCRSAT;
        StencilFail      = KEEP;
        StencilZFail     = DECR;
        StencilRef       = 1;
        StencilMask      = 255;
        StencilWriteMask = 127;
        PixelShader      = compile PS_SHADERMODEL MainPS();
    }
}
