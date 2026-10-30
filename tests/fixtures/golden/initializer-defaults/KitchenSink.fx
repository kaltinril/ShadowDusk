#if defined(OPENGL) || defined(FNA)
#define SV_POSITION POSITION
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

#define TINT float4(0.25, 0.5, 0.75, 1)

static const float K = 0.5;

float4 Tint = TINT;
float Gain = K * 2 + 0.25;
float3 Axis = float3(-1, 0, 1);   // packed behind Gain, ahead of a float4x4 (issue #425)
float4x4 World = float4x4(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16);
float4 NoDefault;

float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
    float4 m = mul(NoDefault, World);
    return Tint * Gain + float4(Axis, 0) + m;
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
