struct VSInput
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float2 TexCoord : TEXCOORD0;
};

float4 VSMain(VSInput input) : SV_Position
{
    return float4(input.Position, 1.0);
}
