// KniEffectTest companion (ShadowDusk-authored): which way round a float4x4 brace-list default lands.
// Only M[0][1] is non-zero, so mul(v, M) = (0, v.x, 0, 0) = (0, 0.5, 0, 0) and the pixel is (0,128,0,255)
// when the default is the HLSL initializer. A default blob written row by row instead of column by column
// puts the 1 at M[1][0] and gives (v.y, 0, 0) = (64,0,0,255): the render tells the two layouts apart.
// KNI's OptimizedMatrix test asks the same question through GetValueMatrix.
#if defined(OPENGL)
#define SV_POSITION POSITION
#define PS_PROFILE ps_3_0
#elif defined(SM6)
#define PS_PROFILE ps_6_0
#else
#define PS_PROFILE ps_4_0_level_9_1
#endif

float4x4 M = {0, 1, 0, 0,
              0, 0, 0, 0,
              0, 0, 0, 0,
              0, 0, 0, 0};

float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
    float4 r = mul(float4(0.5, 0.25, 0.75, 1), M);
    return float4(r.x, r.y, r.z, 1);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_PROFILE MainPS();
    }
};
