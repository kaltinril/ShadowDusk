// Textured + cbuffer: luma cutoff to black/white, Parameters["Cutoff"].
cbuffer Params
{
    float Cutoff;
}

Texture2D SpriteTexture;
SamplerState SpriteSampler;

[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float4 c = SpriteTexture.Sample(SpriteSampler, uv);
    float luma = dot(c.rgb, float3(0.299, 0.587, 0.114));
    float v = step(Cutoff, luma);
    return float4(v, v, v, c.a);
}
