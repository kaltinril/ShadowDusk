#if defined(OPENGL) || defined(FNA)
#define SV_POSITION POSITION
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// Issue #423 follow-up: a TALL row_major float4x3 with a NON-zero fourth row, which MonoGame's DirectX
// upload (3 registers of 4 floats over 4 one-per-row registers) never reaches: there the initializer is
// the SD0017 warning and a zero default, so mul((0,0,0,1), M) = row 3 renders black. Vulkan's 3x4 header
// fills the buffer, so there row 3 is (0.25, 0.5, 0.75): (64, 128, 191, 255). (float4x3: not on GL.)
row_major float4x3 M = float4x3(1,    0,   0,
                                0,    1,   0,
                                0,    0,   1,
                                0.25, 0.5, 0.75);

float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
    float3 r = mul(float4(0, 0, 0, 1), M);
    return float4(r, 1);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
