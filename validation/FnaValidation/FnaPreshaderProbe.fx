// Diagnostic probe (not a product fixture): isolates fxc's PRESHADER path.
//
// fxc /T fx_2_0 hoists uniform-only expressions (probeA + probeB) into a CPU-side
// preshader; vkd3d computes them in-shader. With probeA=0.25, probeB=0.5 the expected
// output is rgb = (0.75, 0.25, 0.5):
//   red   = the preshader-computed sum   (0 here = FNA/MojoShader preshader inert)
//   green = probeA read directly via CTAB (0 here = plain uniform upload broken)
//   blue  = probeB read directly via CTAB
float probeA;
float probeB;

float4 mainPixel(float2 uv : TEXCOORD0) : COLOR0
{
    return float4(probeA + probeB, probeA, probeB, 1);
}

technique T
{
    pass P
    {
        PixelShader = compile ps_3_0 mainPixel();
    }
}
