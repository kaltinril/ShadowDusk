// The Textured pair written by hand as one effect. The pair frontend must compile to the same bytes.
#if SM4
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#else
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#endif

float4x4 WorldViewProjection;

struct VSInput
{
    float4 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float2 TexCoord : TEXCOORD0;
};

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProjection);
    output.TexCoord = input.TexCoord;
    return output;
}
Texture2D SpriteTexture;
SamplerState SpriteSampler;
float4 Tint;

float4 PSMain(VSOutput input) : SV_Target0
{
    return SpriteTexture.Sample(SpriteSampler, input.TexCoord) * Tint;
}

technique HlslStages
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL VSMain();
        PixelShader = compile PS_SHADERMODEL PSMain();
    }
}
