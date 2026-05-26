struct DirectionalLight
{
    float3 Dir;
    float3 Color;
    float  Intensity;
};

cbuffer LightParams : register(b0)
{
    DirectionalLight Light;
}

float4 PSMain() : SV_Target
{
    return float4(Light.Color * Light.Intensity, 1.0);
}
