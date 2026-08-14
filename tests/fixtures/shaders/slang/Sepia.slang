// Textured PS-only: classic sepia weighting.
Texture2D SpriteTexture;
SamplerState SpriteSampler;

[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float4 c = SpriteTexture.Sample(SpriteSampler, uv);
    float3 sepia = float3(
        dot(c.rgb, float3(0.393, 0.769, 0.189)),
        dot(c.rgb, float3(0.349, 0.686, 0.168)),
        dot(c.rgb, float3(0.272, 0.534, 0.131)));
    return float4(saturate(sepia), c.a);
}
