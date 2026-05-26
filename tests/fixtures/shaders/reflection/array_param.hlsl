cbuffer Params : register(b0)
{
    float PointLights[4];
}

float4 PSMain() : SV_Target { return float4(PointLights[0], 0, 0, 1); }
