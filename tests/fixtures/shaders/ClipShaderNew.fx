#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler TextureSampler : register(s0);
Texture2D Mask;
sampler MaskSampler
{
    Texture = (Mask);
    MagFilter = LINEAR;
    MinFilter = LINEAR;
    Mipfilter = LINEAR;
    AddressU = CLAMP;
    AddressV = CLAMP;
};

struct PixelInput
{
    float4 Position : SV_Position0;
    float4 Color : COLOR0;
    float4 TexCoord : TEXCOORD0;
};

float4 SpritePixelShader(PixelInput p) : COLOR0
{
    float4 diffuse = tex2D(TextureSampler, p.TexCoord.xy);
    float4 mask = tex2D(MaskSampler, p.TexCoord.xy);

    return diffuse* float4(mask.r, mask.b, mask.r, mask.a);
}

technique SpriteBatch
{
    pass
    {
        PixelShader = compile PS_SHADERMODEL SpritePixelShader();
    }
}