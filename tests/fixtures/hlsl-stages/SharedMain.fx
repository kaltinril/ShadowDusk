// The SharedMain pair written by hand as one effect; its entry points are the renamed main_VS and main_PS.
#if SM4
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#else
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#endif

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

Varyings main_VS(float4 position : POSITION0, float4 color : COLOR0)
{
    Varyings output;
    output.Position = mul(position, ViewProjection);
    output.Color = color * (1.0 + 0.1 * sin(Time));
    return output;
}
float Exposure;

float4 main_PS(Varyings input) : SV_Target0
{
    return input.Color * Tint * Exposure;
}

technique HlslStages
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL main_VS();
        PixelShader = compile PS_SHADERMODEL main_PS();
    }
}
