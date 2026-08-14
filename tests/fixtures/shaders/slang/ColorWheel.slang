// Uniform-free procedural: angle-driven color via atan2 (exercises a trig intrinsic
// whose HLSL and GLSL argument conventions differ - a real transpilation hazard).
[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float2 p = uv - float2(0.5, 0.5);
    float angle = atan2(p.y, p.x) / 6.2831853 + 0.5;
    float r = saturate(abs(angle * 6.0 - 3.0) - 1.0);
    float g = saturate(2.0 - abs(angle * 6.0 - 2.0));
    float b = saturate(2.0 - abs(angle * 6.0 - 4.0));
    return float4(r, g, b, 1);
}
