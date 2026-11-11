// The Sm3 pair written by hand as one effect.
#if SM4
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#else
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#endif

float4x4 WorldViewProjection;

struct VSOutput
{
    float4 Position : POSITION0;
    float2 UV : TEXCOORD0;
};

VSOutput VSMain(float4 position : POSITION0, float2 uv : TEXCOORD0)
{
    VSOutput output;
    output.Position = mul(position, WorldViewProjection);
    output.UV = uv;
    return output;
}
sampler2D TexSampler;
float4 Tint;

float4 PSMain(float2 uv : TEXCOORD0) : COLOR0
{
    return tex2D(TexSampler, uv) * Tint;
}

technique HlslStages
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL VSMain();
        PixelShader = compile PS_SHADERMODEL PSMain();
    }
}
