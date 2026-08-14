// Textured + cbuffer: the consumer drives Tint via Parameters["Tint"].
cbuffer Params
{
    float4 Tint;
}

Texture2D SpriteTexture;
SamplerState SpriteSampler;

[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    return SpriteTexture.Sample(SpriteSampler, uv) * Tint;
}
