// =============================================================================
// GaussianBlur.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : Nez (prime31/Nez)
//   Repo       : https://github.com/prime31/Nez
//   Commit     : 6c9d4a87ac62ce36e217cb5e4bbe36d1769dfa4c
//   Upstream   : DefaultContentSource/effects/GaussianBlur.fx
//   License    : MIT - Copyright (c) 2016 Mike (see ./LICENSE, ./NOTICE.md)
//   Exercises  : 1-D N-tap gaussian blur: a literal-bounded for-loop over float2[]/float[] array uniforms.
// =============================================================================
// Pixel shader applies a one dimensional gaussian blur filter. This is used twice by the bloom postprocess, first to
// blur horizontally, and then again to blur vertically.

sampler s0; // from SpriteBatch

#define SAMPLE_COUNT 15

float2 _sampleOffsets[SAMPLE_COUNT];
float _sampleWeights[SAMPLE_COUNT];


float4 PixelShaderFunction( float2 texCoord : TEXCOORD0 ) : COLOR0
{
    float4 c = 0;
    
    // Combine a number of weighted image filter taps.
    for( int i = 0; i < SAMPLE_COUNT; i++ )
        c += tex2D( s0, texCoord + _sampleOffsets[i] ) * _sampleWeights[i];
    
    return c;
}


technique GaussianBlur
{
    pass Pass1
    {
        PixelShader = compile ps_2_0 PixelShaderFunction();
    }
}
