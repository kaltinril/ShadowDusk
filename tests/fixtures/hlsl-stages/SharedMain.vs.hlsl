// Both entry points are called main. The cbuffer and the struct are shared and identical; Time is
// vertex-only.
cbuffer Frame
{
    float4x4 ViewProjection;
    float4 Tint;
};

float Time;

struct Varyings
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
};

Varyings main(float4 position : POSITION0, float4 color : COLOR0)
{
    Varyings output;
    output.Position = mul(position, ViewProjection);
    output.Color = color * (1.0 + 0.1 * sin(Time));
    return output;
}
