// Uniform-free procedural: hard-edged diagonal stripes via step(frac()).
[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float band = step(0.5, frac((uv.x + uv.y) * 6.0));
    return float4(band, 1.0 - band, 0.2, 1);
}
