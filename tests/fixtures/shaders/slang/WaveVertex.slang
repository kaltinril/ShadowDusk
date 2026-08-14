// VS+PS: the vertex shader displaces Y by a sine of X (amplitude/frequency from the
// cbuffer), the pixel shader passes the interpolated color through. Exercises a real
// vertex stage plus interpolants on the Slang route.
cbuffer Params
{
    float4x4 WorldViewProjection;
    float2 Wave; // x = frequency, y = amplitude
}

struct VSInput
{
    float4 Position : POSITION;
    float4 Color    : COLOR0;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float4 Color    : COLOR0;
};

[shader("vertex")]
VSOutput MainVS(VSInput input)
{
    VSOutput o;
    float4 p = input.Position;
    p.y += sin(p.x * Wave.x) * Wave.y;
    o.Position = mul(p, WorldViewProjection);
    o.Color = input.Color;
    return o;
}

[shader("fragment")]
float4 MainPS(VSOutput input) : SV_Target
{
    return input.Color;
}
