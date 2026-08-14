// Textured PS-only: color inversion of the sampled texel.
Texture2D SpriteTexture;
SamplerState SpriteSampler;

[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float4 c = SpriteTexture.Sample(SpriteSampler, uv);
    return float4(1.0 - c.rgb, c.a);
}
