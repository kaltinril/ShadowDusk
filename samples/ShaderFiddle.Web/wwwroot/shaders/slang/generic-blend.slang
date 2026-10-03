// Full Slang (issue #257): an interface, two conforming blend modes, and a generic function
// constrained on the interface. None of this has an HLSL spelling, so it only compiles
// through real slangc; in this sample slangc runs in the page as WebAssembly and its HLSL
// goes through the same DXC pipeline as every .fx.

interface IBlendMode
{
    float3 blend(float3 baseColor, float3 blendColor);
}

struct MultiplyBlend : IBlendMode
{
    float3 blend(float3 baseColor, float3 blendColor) { return baseColor * blendColor; }
}

struct ScreenBlend : IBlendMode
{
    float3 blend(float3 baseColor, float3 blendColor)
    {
        return 1.0 - (1.0 - baseColor) * (1.0 - blendColor);
    }
}

float3 applyBlend<T : IBlendMode>(T mode, float3 baseColor, float3 blendColor)
{
    return mode.blend(baseColor, blendColor);
}

Texture2D SpriteTexture;
SamplerState SpriteTextureSampler;

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
    float3 tint = float3(input.TexCoord.x, input.TexCoord.y, 1.0 - input.TexCoord.x);
    MultiplyBlend multiply;
    ScreenBlend screen;
    float3 rgb = input.TexCoord.x < 0.5
        ? applyBlend(multiply, tex.rgb, tint)
        : applyBlend(screen, tex.rgb, tint * 0.5);
    return float4(rgb, tex.a);
}
