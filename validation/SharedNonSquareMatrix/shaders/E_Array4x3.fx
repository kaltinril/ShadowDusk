#if VULKAN
#define PS ps_6_0
#else
#define PS ps_4_0
#endif

// float4x3 B[2] (the stock SkinnedEffect Bones shape): pixel i = row (i % 4) of B[i / 4].
float4x3 B[2];

float4 MainPS(float4 pos : SV_Position) : SV_Target0
{
    int i = (int)pos.x;
    int r = i % 4;
    float4 e = float4(r == 0, r == 1, r == 2, r == 3);
    float3 v = (i < 4) ? mul(e, B[0]) : mul(e, B[1]);
    return float4(v, 1);
}

technique T { pass P { PixelShader = compile PS MainPS(); } }
