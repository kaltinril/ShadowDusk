#if VULKAN
#define PS ps_6_0
#else
#define PS ps_4_0
#endif

// Buffer size: a float2 then a float2x2 LAST in the globals. Pixel i = (V, row i of M).
float2 V;
float2x2 M;

float4 MainPS(float4 pos : SV_Position) : SV_Target0
{
    int i = (int)pos.x;
    float2 e = float2(i == 0, i == 1);
    return float4(V, mul(e, M));
}

technique T { pass P { PixelShader = compile PS MainPS(); } }
