// =============================================================================
// Bevels.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : Nez (prime31/Nez)
//   Repo       : https://github.com/prime31/Nez
//   Commit     : 6c9d4a87ac62ce36e217cb5e4bbe36d1769dfa4c
//   Upstream   : DefaultContentSource/effects/Bevels.fx
//   License    : MIT - Copyright (c) 2016 Mike (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Neighbor-tap edge-detect / emboss (offset tex2D taps, no loop).
// =============================================================================
sampler s0;

float4 PixelShaderFunction(float2 coords: TEXCOORD0) : COLOR0
{
    float4 color = tex2D(s0, coords);
	color -= tex2D(s0, coords - 0.002) * 2.5f;
	color += tex2D(s0, coords + 0.002) * 2.5f;

    return color;
}

technique Technique1
{
    pass Pass1
    {
        PixelShader = compile ps_2_0 PixelShaderFunction();
    }
}