#if defined(OPENGL) || defined(FNA)
#define SV_POSITION POSITION
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// Issue #423 follow-up: a TALL row_major float4x3 whose upper 3x3 is NOT symmetric, so a default that
// only works for a symmetric matrix (the column-by-column blob, read back transposed) renders
// mul(v, Mt) = (0.25, 0.25, 0.125), (64, 64, 32). The rows written in register order give
// mul(v, M) = (0.125, 0.5, 0.125): (32, 128, 32, 255). (float4x3: not on GL, SD0210.)
row_major float4x3 M = float4x3(0,   1, 0,
                                0.5, 0, 0,
                                0,   0, 1,
                                0,   0, 0);

float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
    float3 r = mul(float4(0.5, 0.25, 0.125, 1), M);
    return float4(r, 1);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
