#if VULKAN
#define PS ps_6_0
#else
#define PS ps_4_0
#endif

// float4x3 followed by a float4: pixels 0-3 = rows of M, pixel 4 = G (the register a
// 4-register upload of M would spill into).
float4x3 M;
float4 G;

float4 MainPS(float4 pos : SV_Position) : SV_Target0
{
    int i = (int)pos.x;
    float4 e = float4(i == 0, i == 1, i == 2, i == 3);
    return (i < 4) ? float4(mul(e, M), 1) : G;
}

technique T { pass P { PixelShader = compile PS MainPS(); } }
