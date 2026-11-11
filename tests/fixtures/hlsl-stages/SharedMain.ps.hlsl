// Exposure is pixel-only; the cbuffer and the struct repeat the vertex source's declarations.
cbuffer Frame
{
    float4x4 ViewProjection;
    float4 Tint;
};

float Exposure;

struct Varyings
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
};

float4 main(Varyings input) : SV_Target0
{
    return input.Color * Tint * Exposure;
}
