// Textured PS-only: darkening toward the corners, no uniforms beyond the texture.
Texture2D SpriteTexture;
SamplerState SpriteSampler;

[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float4 c = SpriteTexture.Sample(SpriteSampler, uv);
    float d = distance(uv, float2(0.5, 0.5));
    float fade = saturate(1.0 - d * d * 2.5);
    return float4(c.rgb * fade, c.a);
}
