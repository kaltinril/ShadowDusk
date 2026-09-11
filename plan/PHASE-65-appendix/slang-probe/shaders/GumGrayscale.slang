// Gum-shaped probe #1: a fixed-recipe post-process fragment shader, the shape Gum's own
// SkiaGum/Grayscale.sksl and its .fx twin (Phase 62 §2.6) already cover. Single texture
// sample, no vertex stage (SpriteBatch-style). HLSL-compatible Slang throughout.

Texture2D SpriteTexture : register(t0);
SamplerState SpriteTextureSampler : register(s0);

cbuffer Params : register(b0)
{
    float Strength;
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
    float gray = dot(tex.rgb, float3(0.299, 0.587, 0.114));
    float3 result = lerp(tex.rgb, float3(gray, gray, gray), Strength);
    return float4(result, tex.a);
}
