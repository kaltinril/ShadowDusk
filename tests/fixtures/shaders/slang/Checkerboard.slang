// Uniform-free procedural: 8x8 checkerboard via floor parity.
[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float2 cell = floor(uv * 8.0);
    float parity = frac((cell.x + cell.y) * 0.5) * 2.0;
    return float4(parity, parity, parity, 1);
}
