// Uniform-free procedural: an anti-aliased circle via smoothstep over a signed distance.
[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float d = distance(uv, float2(0.5, 0.5)) - 0.3;
    float edge = smoothstep(0.02, -0.02, d);
    return float4(edge, edge, 0.1 + 0.9 * edge, 1);
}
