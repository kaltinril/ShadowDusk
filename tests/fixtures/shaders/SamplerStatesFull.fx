// Phase 43 (F9) corpus fixture: sampler_state blocks with filter/address/border/
// anisotropy/LOD members baked into the .mgfx sampler records (hasState = 1).
// Two samplers probe both halves of mgfxc's SamplerStateInfo behavior:
//   - PointSampler: Point min/mag + MipFilter = NONE (forces the -16 LOD-bias
//     mip-disable) + Clamp/Mirror addressing.
//   - AnisoSampler: MinFilter = ANISOTROPIC (wins the filter chain) + MaxAnisotropy,
//     Border addressing with a BorderColor (0xRRGGBBAA per mgfxc's ParseColor),
//     MaxMipLevel and an explicit MipLodBias.
// PS-only SpriteBatch shape, mgfxc-compilable for golden generation.
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;
Texture2D DetailTexture;

sampler2D PointSampler = sampler_state
{
    Texture   = <SpriteTexture>;
    MinFilter = POINT;
    MagFilter = POINT;
    MipFilter = NONE;
    AddressU  = CLAMP;
    AddressV  = MIRROR;
};

sampler2D AnisoSampler = sampler_state
{
    Texture       = <DetailTexture>;
    MinFilter     = ANISOTROPIC;
    MaxAnisotropy = 2;
    AddressU      = BORDER;
    AddressV      = BORDER;
    BorderColor   = 0xff0000ff;
    MaxMipLevel   = 1;
    MipLodBias    = 0.5;
};

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR
{
    float4 a = tex2D(PointSampler, input.TextureCoordinates);
    float4 b = tex2D(AnisoSampler, input.TextureCoordinates);
    return (a * 0.75 + b * 0.25) * input.Color;
}

technique SamplerStates
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
