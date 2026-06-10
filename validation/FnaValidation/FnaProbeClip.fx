// Diagnostic probe (not a product fixture): branch + discard on a spatially-varying
// texture value vs a uniform-derived threshold — Dissolve's exact kill shape
// (`if (dissolveAmount < progress - _dissolveThreshold) discard;`).
//
// With probeA=0.25, probeB=0.5: pixels where (1 - cat.r) < 0.25 (bright-red areas,
// ~34% of the cat) must be discarded — leaving the primed PLAIN-cat pixels — while
// survivors return the INVERTED color. The inversion is load-bearing: if survivors
// returned the sampled color unchanged, a never-firing discard would be invisible
// (killed pixels show the primed cat = the same pixels) and the probe would pass
// vacuously. A diff means the candidate's kill branch (cmp -> ifc_ne x,-x ->
// mov + texkill arrangement) mistranslated.
sampler s0;
float probeA;
float probeB;

float4 mainPixel(float2 uv : TEXCOORD0) : COLOR0
{
    float4 c = tex2D(s0, uv);
    float a = 1 - c.r;
    if (a < probeB - probeA)
        discard;
    return float4(1 - c.rgb, 1);
}

technique T
{
    pass P
    {
        PixelShader = compile ps_3_0 mainPixel();
    }
}
