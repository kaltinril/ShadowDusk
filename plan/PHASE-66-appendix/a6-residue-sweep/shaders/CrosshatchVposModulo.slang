// A6 residue sweep sample, derived from tests/fixtures/shaders/third-party/Nez/Crosshatch.fx.
// Exercises: the VPOS semantic on a pixel-shader input parameter, the `%` modulo
// operator, an `int` uniform, nested `if` branches, bare `sampler` + `tex2D()`.
sampler s0;

int crossHatchSize; // 16

[shader("fragment")]
float4 PixelShaderFunction(float2 coords : TEXCOORD0, in float2 screenPos : VPOS) : SV_Target
{
    float halfCrossHatchSize = crossHatchSize * 0.5;
    float lum = length(tex2D(s0, coords).rgb);
    float4 color = float4(1.0, 1.0, 1.0, 1.0);

    if (lum < 1.0)
    {
        if ((screenPos.x + screenPos.y) % crossHatchSize == 0.0)
            color = float4(0.0, 0.0, 0.0, 1.0);
    }

    if (lum < 0.75)
    {
        if ((screenPos.x - screenPos.y) % crossHatchSize == 0.0)
            color = float4(0.0, 0.0, 0.0, 1.0);
    }

    if (lum < 0.5)
    {
        if ((screenPos.x + screenPos.y - halfCrossHatchSize) % crossHatchSize == 0.0)
            color = float4(0.0, 0.0, 0.0, 1.0);
    }

    if (lum < 0.3)
    {
        if ((screenPos.x - screenPos.y - halfCrossHatchSize) % crossHatchSize == 0.0)
            color = float4(0.0, 0.0, 0.0, 1.0);
    }

    return color;
}
