#if VULKAN
#define PS ps_6_0
#else
#define PS ps_4_0
#endif

// float2x3: pixel i = row i of M.
float2x3 M;

float4 MainPS(float4 pos : SV_Position) : SV_Target0
{
    int i = (int)pos.x;
    float2 e = float2(i == 0, i == 1);
    return float4(mul(e, M), 1);
}

technique T { pass P { PixelShader = compile PS MainPS(); } }
