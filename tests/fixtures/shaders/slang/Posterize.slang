// Textured + cbuffer: quantize each channel to Parameters["Levels"] steps.
cbuffer Params
{
    float Levels;
}

Texture2D SpriteTexture;
SamplerState SpriteSampler;

[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float4 c = SpriteTexture.Sample(SpriteSampler, uv);
    float3 q = floor(c.rgb * Levels) / max(Levels - 1.0, 1.0);
    return float4(saturate(q), c.a);
}
