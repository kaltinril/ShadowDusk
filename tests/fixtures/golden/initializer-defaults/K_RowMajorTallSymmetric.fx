#if defined(OPENGL) || defined(FNA)
#define SV_POSITION POSITION
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// Issue #423 follow-up: a TALL row_major float4x3 (more rows than columns), symmetric upper 3x3 and a
// zero fourth row, the shape of an affine world or bone matrix. On DirectX MonoGame uploads 3 registers
// of 4 floats over 4 one-per-row registers, which reaches rows 0..2; the zero row 3 needs nothing.
// mul((1,1,1,1), M) = (0.25, 0.5, 0.75): (64, 128, 191, 255). (float4x3: not on GL, SD0210.)
row_major float4x3 M = float4x3(0.25, 0,   0,
                                0,    0.5, 0,
                                0,    0,   0.75,
                                0,    0,   0);

float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
    float3 r = mul(float4(1, 1, 1, 1), M);
    return float4(r, 1);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
