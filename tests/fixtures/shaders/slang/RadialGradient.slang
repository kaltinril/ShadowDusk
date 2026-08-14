// Uniform-free procedural: brightness falls off with distance from center.
[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float d = distance(uv, float2(0.5, 0.5));
    float v = saturate(1.0 - d * 2.0);
    return float4(v, v * 0.5, 1.0 - v, 1);
}
