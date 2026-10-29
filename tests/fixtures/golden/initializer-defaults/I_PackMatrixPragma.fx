#if defined(OPENGL) || defined(FNA)
#define SV_POSITION POSITION
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// Issue #423 follow-up: MatrixDefault.fx under #pragma pack_matrix(row_major), which makes every
// matrix after it row_major without a modifier. Expected (0, 128, 0, 255); transposed is (64, 0, 0, 255).
#pragma pack_matrix(row_major)
float4x4 M = float4x4(0, 1, 0, 0,
                      0, 0, 0, 0,
                      0, 0, 0, 0,
                      0, 0, 0, 0);

float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
    float4 r = mul(float4(0.5, 0.25, 0.75, 1), M);
    return float4(r.x, r.y, r.z, 1);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
