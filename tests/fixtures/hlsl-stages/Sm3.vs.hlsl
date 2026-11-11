// A Shader Model 3 style pair, the dialect the FNA target (fx_2_0) reads.
float4x4 WorldViewProjection;

struct VSOutput
{
    float4 Position : POSITION0;
    float2 UV : TEXCOORD0;
};

VSOutput VSMain(float4 position : POSITION0, float2 uv : TEXCOORD0)
{
    VSOutput output;
    output.Position = mul(position, WorldViewProjection);
    output.UV = uv;
    return output;
}
