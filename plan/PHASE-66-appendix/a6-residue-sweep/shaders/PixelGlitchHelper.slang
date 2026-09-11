// A6 residue sweep sample, derived from tests/fixtures/shaders/third-party/Nez/PixelGlitch.fx.
// Exercises: a free-function helper called from the entry point, dot()/frac()/floor(),
// bare `sampler` + `tex2D()`.
sampler s0;

float _verticalSize;
float _horizontalOffset;
float2 _screenSize;

float hash11(float p)
{
    float3 p3 = frac(float3(p, p, p) * 0.1031);
    p3 += dot(p3, p3.yzx + 19.19);
    return frac((p3.x + p3.y) * p3.z);
}

[shader("fragment")]
float4 MainPS(float2 coords : TEXCOORD0) : SV_Target
{
    float _pixels = _screenSize.x / _verticalSize;
    float offset = _horizontalOffset / _screenSize.y;

    float r = hash11(floor(coords.y * _pixels)) * 2.0 - 1.0;
    return tex2D(s0, float2(coords.x + r * offset, coords.y));
}
