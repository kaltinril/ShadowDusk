#if VULKAN
#define PS ps_6_0
#else
#define PS ps_4_0
#endif

// Buffer size: a float4 then a row_major float2x3 LAST in the globals (more columns than rows).
// Pixels 0-1 = rows of M, pixel 2 = Pad.
float4 Pad;
row_major float2x3 M;

float4 MainPS(float4 pos : SV_Position) : SV_Target0
{
    int i = (int)pos.x;
    float2 e = float2(i == 0, i == 1);
    return (i < 2) ? float4(mul(e, M), 1) : Pad;
}

technique T { pass P { PixelShader = compile PS MainPS(); } }
