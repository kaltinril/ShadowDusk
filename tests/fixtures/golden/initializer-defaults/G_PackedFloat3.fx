#if defined(OPENGL) || defined(FNA)
#define SV_POSITION POSITION
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// Issue #425: HLSL packing puts Axis at byte 4, in Gain's register, which failed the OpenGL compile
// (SPIRV-Cross's std140 refusal, SD0100). On OpenGL each now has its own register. The pixel tells a
// misplaced member apart: Axis * Gain * 2 = (0.25, 0.5, 1) and World swaps x and y, so the pixel is
// (128, 64, 255, 255). Axis read from Gain's register gives (0, 0, 0); Gain read as 0 gives (0, 0, 0).
float Gain = 0.5;
float3 Axis = float3(0.25, 0.5, 1.0);
float4x4 World = float4x4(0, 1, 0, 0,
                          1, 0, 0, 0,
                          0, 0, 1, 0,
                          0, 0, 0, 1);

float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
    float4 v = mul(float4(Axis * Gain * 2, 1), World);
    return float4(v.x, v.y, v.z, 1);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
