// Gum-shaped probe #2: uniform-driven tint, same recipe shape as ShadowDusk's own
// tests/fixtures/shaders/TintShader.fx (vendored precisely because Gum-shaped fixed
// recipes like this are common). HLSL-compatible Slang.

Texture2D SpriteTexture : register(t0);
SamplerState SpriteTextureSampler : register(s0);

cbuffer Params : register(b0)
{
    float3 TintColor;
    float TintAmount;
};

struct PsInput
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

[shader("fragment")]
float4 MainPS(PsInput input) : SV_Target0
{
    float4 tex = SpriteTexture.Sample(SpriteTextureSampler, input.TexCoord) * input.Color;
    float3 tinted = lerp(tex.rgb, tex.rgb * TintColor, TintAmount);
    return float4(tinted, tex.a);
}
