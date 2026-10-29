#if defined(OPENGL) || defined(FNA)
#define SV_POSITION POSITION
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// Issue #423 follow-up: an ARRAY of tall row_major float4x3 (zero fourth rows). MonoGame uploads each
// element ColumnCount registers after the last; on DirectX that is 3, while the buffer holds 4 per
// element, so B[1] would land one register early and over B[0]'s zero row: there the initializer is the
// SD0017 warning and a zero default, black. Vulkan's 3x4 header advances 4 registers per element, so
// there mul(v, B[1]) = (0.125, 0.5, 0.125): (32, 128, 32, 255). (float4x3: not on GL, SD0210.)
row_major float4x3 B[2] = {
    float4x3(0.25, 0,   0,
             0,    0.5, 0,
             0,    0,   0.75,
             0,    0,   0),
    float4x3(0,    1,   0,
             0.5,  0,   0,
             0,    0,   1,
             0,    0,   0)
};

float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
    float3 r = mul(float4(0.5, 0.25, 0.125, 1), B[1]);
    return float4(r, 1);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
