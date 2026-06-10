// Diagnostic probe (not a product fixture): comparison-to-float (drives D3D9 `cmp`).
//
// With probeA=0.25, probeB=0.5 the expected output is rgb = (1, 0, 0) + a=1:
//   red   = probeA <  probeB  (true  -> 1)
//   green = probeA >= probeB  (false -> 0)
// fxc preshader-folds the uniform-only expressions; vkd3d computes them in-shader
// via cmp — a pixel diff means MojoShader mistranslates vkd3d's cmp arrangement.
float probeA;
float probeB;

float4 mainPixel(float2 uv : TEXCOORD0) : COLOR0
{
    float lt = probeA < probeB ? 1.0 : 0.0;
    float ge = probeA >= probeB ? 1.0 : 0.0;
    return float4(lt, ge, 0, 1);
}

technique T
{
    pass P
    {
        PixelShader = compile ps_3_0 mainPixel();
    }
}
