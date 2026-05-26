cbuffer Params : register(b0)
{
    float    Scale;
    float3   Direction;
    float4   Color;
    float4x4 World;
}

float4 PSMain() : SV_Target { return Color * Scale; }
