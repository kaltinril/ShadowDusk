// VS+PS: the vertex shader offsets the texture coordinate by a cbuffer scroll vector;
// the pixel shader samples there. Exercises a VS-written interpolant feeding a sample.
cbuffer Params
{
    float4x4 WorldViewProjection;
    float2 ScrollOffset;
}

Texture2D SpriteTexture;
SamplerState SpriteSampler;

struct VSInput
{
    float4 Position : POSITION;
    float2 UV       : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float2 UV       : TEXCOORD0;
};

[shader("vertex")]
VSOutput MainVS(VSInput input)
{
    VSOutput o;
    o.Position = mul(input.Position, WorldViewProjection);
    o.UV = input.UV + ScrollOffset;
    return o;
}

[shader("fragment")]
float4 MainPS(VSOutput input) : SV_Target
{
    return SpriteTexture.Sample(SpriteSampler, input.UV);
}
