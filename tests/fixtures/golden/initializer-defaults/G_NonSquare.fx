#if defined(OPENGL) || defined(FNA)
#define SV_POSITION POSITION
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// Issue #423: non-square matrix defaults. Every element is distinct, and the three read are the only
// ones with their value, so a transposed shape or a blob written row by row cannot give (191,128,64).
float2x3 Wide = float2x3(0.125, 0.875, 0.75,
                         0.375, 0.5,   0.625);
float3x2 Tall = float3x2(0.1, 0.2,
                         0.3, 0.4,
                         0.25, 0.6);
float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
    return float4(Wide[0].z, Wide[1].y, Tall[2].x, 1);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
