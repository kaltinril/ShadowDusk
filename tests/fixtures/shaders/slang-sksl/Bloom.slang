// Slang twin of Saturate.fx (the bloom pass).
float4 BloomThreshold;
float BloomIntensity;
float BloomSaturation;

Texture2D TextureSamplerTexture;
SamplerState TextureSampler;

[shader("fragment")]
float4 BloomPass(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float4 color = TextureSamplerTexture.Sample(TextureSampler, uv);
    color = saturate(color - BloomThreshold) * BloomIntensity + color;
    color = saturate(color);
    color = lerp(color, color.rgba + color.rgba * BloomSaturation, BloomSaturation);
    return color;
}
