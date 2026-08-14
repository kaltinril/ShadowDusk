// Uniform-free procedural: layered sines (a static plasma). Exercises nested
// transcendentals and mixed-frequency arithmetic.
[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float v = sin(uv.x * 10.0)
            + sin(uv.y * 13.0)
            + sin((uv.x + uv.y) * 11.0)
            + sin(distance(uv, float2(0.3, 0.7)) * 17.0);
    v = v * 0.125 + 0.5;
    return float4(v, 1.0 - v, 0.5 + 0.5 * sin(v * 6.2831853), 1);
}
