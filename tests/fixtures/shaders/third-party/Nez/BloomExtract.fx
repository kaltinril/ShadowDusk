// =============================================================================
// BloomExtract.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : Nez (prime31/Nez)
//   Repo       : https://github.com/prime31/Nez
//   Commit     : 6c9d4a87ac62ce36e217cb5e4bbe36d1769dfa4c
//   Upstream   : DefaultContentSource/effects/BloomExtract.fx
//   License    : MIT - Copyright (c) 2016 Mike (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Bloom bright-pass extract; saturate() threshold remap.
// =============================================================================
// Pixel shader extracts the brighter areas of an image.. This is the first step in applying a bloom postprocess.

sampler s0; // from SpriteBatch

float _bloomThreshold;


float4 PixelShaderFunction( float2 texCoord : TEXCOORD0 ) : COLOR0
{
    // Look up the original image color.
    float4 c = tex2D( s0, texCoord );

    // Adjust it to keep only values brighter than the specified threshold.
    return saturate( ( c - _bloomThreshold ) / ( 1 - _bloomThreshold ) );
}


technique BloomExtract
{
    pass Pass1
    {
        PixelShader = compile ps_2_0 PixelShaderFunction();
    }
}
