// A genuinely Slang-only feature, hand-written to be natural for a Gum-shaped blend/tint
// effect: a generic blend function over the scalar type, plus a Slang `interface` a concrete
// blend mode conforms to. Neither construct has an HLSL spelling - the shipped SlangFrontend
// (SD0600) rejects `__generic`/`extension` by name; this file measures whether real slangc
// accepts it and what HLSL (if any) falls out the other side.

interface IBlendMode
{
    float3 blend(float3 baseColor, float3 blendColor);
}

struct MultiplyBlend : IBlendMode
{
    float3 blend(float3 baseColor, float3 blendColor)
    {
        return baseColor * blendColor;
    }
}

// Generic over any type conforming to IBlendMode - genuinely not expressible in HLSL, which
// has no interfaces/generics on free functions.
float3 applyBlend<T : IBlendMode>(T mode, float3 baseColor, float3 blendColor)
{
    return mode.blend(baseColor, blendColor);
}

Texture2D SpriteTexture : register(t0);
SamplerState SpriteTextureSampler : register(s0);

cbuffer Params : register(b0)
{
    float3 BlendColor;
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
    MultiplyBlend mode;
    float3 blended = applyBlend(mode, tex.rgb, BlendColor);
    return float4(blended, tex.a);
}
