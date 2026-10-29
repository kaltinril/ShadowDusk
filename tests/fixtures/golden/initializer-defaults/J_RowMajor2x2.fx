#if defined(OPENGL) || defined(FNA)
#define SV_POSITION POSITION
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// Issue #423 follow-up: a row_major float2x2 with only M[0][1] set (float2x2: not on GL, SD0210).
// mul(v, M) = (0, v[0]) = (0, 0.5), so (0, 128, 0, 255); a transposed default gives (v[1], 0) = (64, 0, 0, 255).
row_major float2x2 M = float2x2(0, 1,
                                0, 0);

float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
    float2 r = mul(float2(0.5, 0.25), M);
    return float4(r.x, r.y, 0, 1);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
