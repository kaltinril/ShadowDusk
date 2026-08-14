// A Slang effect in Slang's own idiom: [shader(...)] attributes mark the entry points.
cbuffer Params
{
    float4x4 WorldViewProjection;
    float Desaturation;
}

Texture2D SpriteTexture;
SamplerState SpriteSampler;

struct VSInput
{
    float4 Position : POSITION;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

[shader("vertex")]
VSOutput MainVS(VSInput input)
{
    VSOutput o;
    o.Position = mul(input.Position, WorldViewProjection);
    o.Color = input.Color;
    o.UV = input.UV;
    return o;
}

[shader("fragment")]
float4 MainPS(VSOutput input) : SV_Target
{
    float4 c = SpriteTexture.Sample(SpriteSampler, input.UV) * input.Color;
    float g = dot(c.rgb, float3(0.299, 0.587, 0.114));
    return lerp(c, float4(g, g, g, c.a), Desaturation);
}
