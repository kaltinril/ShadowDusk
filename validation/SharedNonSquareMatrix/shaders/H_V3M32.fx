#if VULKAN
#define PS ps_6_0
#else
#define PS ps_4_0
#endif

// Buffer size: a float3 then a float3x2 LAST in the globals. Pixel i = (row i of M, V[i], 1).
float3 V;
float3x2 M;

float4 MainPS(float4 pos : SV_Position) : SV_Target0
{
    int i = (int)pos.x;
    float3 e = float3(i == 0, i == 1, i == 2);
    return float4(mul(e, M), dot(e, V), 1);
}

technique T { pass P { PixelShader = compile PS MainPS(); } }
