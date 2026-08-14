// Uniform-free procedural: concentric rings from a sine over distance.
[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float d = distance(uv, float2(0.5, 0.5));
    float ring = 0.5 + 0.5 * sin(d * 60.0);
    return float4(ring, ring * 0.3, 1.0 - ring, 1);
}
