#if defined(OPENGL) || defined(FNA)
#define SV_POSITION POSITION
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// Only M[0][1] is non-zero. mul(v, M)[1] = v[0] * M[0][1] = 0.5, so the pixel is (0, 128, 0, 255).
// A default blob written row by row instead of column by column puts the 1 at M[1][0] and gives
// mul(v, M)[0] = v[1] = 0.25, a (64, 0, 0, 255) pixel: the render tells the two layouts apart.
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
