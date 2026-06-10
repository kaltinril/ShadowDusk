// Diagnostic probe (not a product fixture): a real branch on a uniform comparison
// (drives D3D9 `ifc` / `if`+predicate forms).
//
// With probeA=0.25, probeB=0.5: probeA < probeB is true, so the expected output is
// GREEN (0,1,0,1). RED means the branch condition mistranslated.
float probeA;
float probeB;

float4 mainPixel(float2 uv : TEXCOORD0) : COLOR0
{
    if (probeA < probeB)
        return float4(0, 1, 0, 1);
    else
        return float4(1, 0, 0, 1);
}

technique T
{
    pass P
    {
        PixelShader = compile ps_3_0 mainPixel();
    }
}
