// Phase 43 (F1) corpus fixture: in-pass RASTERIZER states, including NEGATIVE
// float values for DepthBias/SlopeScaleDepthBias (the render-state lexer accepts
// signed numbers; mgfxc's Number token is [+-]?...). PS-only SpriteBatch shape,
// mgfxc-compilable for golden generation.
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

technique Rasterizer
{
    pass P0
    {
        CullMode             = CW;
        FillMode             = SOLID;
        MultiSampleAntiAlias = FALSE;
        ScissorTestEnable    = FALSE;
        DepthBias            = -0.25;
        SlopeScaleDepthBias  = -1.5;
        PixelShader          = compile PS_SHADERMODEL MainPS();
    }
}
