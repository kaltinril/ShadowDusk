// Diagnostic probe (not a product fixture): bool-typed comparison used as a lerp
// factor — the exact shape of Dissolve's `bool b = dissolveAmount < progress;
// return lerp(color, color * thresholdColor, b);`.
//
// With probeA=0.25, probeB=0.5: b = (probeA < probeB) = true, so the expected
// output is GREEN (0,1,0,1). RED means b evaluated false/garbage under
// MojoShader's translation of the candidate bytecode.
float probeA;
float probeB;

float4 mainPixel(float2 uv : TEXCOORD0) : COLOR0
{
    bool b = probeA < probeB;
    return lerp(float4(1, 0, 0, 1), float4(0, 1, 0, 1), b);
}

technique T
{
    pass P
    {
        PixelShader = compile ps_3_0 mainPixel();
    }
}
