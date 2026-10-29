#if VULKAN
#define PS ps_6_0
#else
#define PS ps_4_0
#endif

// float4x3: pixel i = row i of M (mul by a one-hot row vector).
float4x3 M;

float4 MainPS(float4 pos : SV_Position) : SV_Target0
{
    int i = (int)pos.x;
    float4 e = float4(i == 0, i == 1, i == 2, i == 3);
    return float4(mul(e, M), 1);
}

technique T { pass P { PixelShader = compile PS MainPS(); } }
