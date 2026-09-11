// Gum-shaped probe #3: a small fixed-radius box blur, single texture, uniform texel size -
// the shape of a UI-library post-process blur template. HLSL-compatible Slang.

Texture2D SpriteTexture : register(t0);
SamplerState SpriteTextureSampler : register(s0);

cbuffer Params : register(b0)
{
    float2 TexelSize;
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
    float4 sum = float4(0, 0, 0, 0);
    int count = 0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float2 offset = float2(x, y) * TexelSize;
            sum += SpriteTexture.Sample(SpriteTextureSampler, input.TexCoord + offset);
            count++;
        }
    }
    return (sum / count) * input.Color;
}
