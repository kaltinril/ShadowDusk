// =============================================================================
// Noise.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : Nez (prime31/Nez)
//   Repo       : https://github.com/prime31/Nez
//   Commit     : 6c9d4a87ac62ce36e217cb5e4bbe36d1769dfa4c
//   Upstream   : DefaultContentSource/effects/Noise.fx
//   License    : MIT - Copyright (c) 2016 Mike (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Film-grain noise; helper fn rand() (frac/sin/dot) called from the entry.
// =============================================================================
sampler s0;

float noise; // 1.0

float rand( float2 co )
{
    return frac( sin( dot( co.xy, float2( 12.9898, 78.233 ) ) ) * 43758.5453 );
}


float4 PixelShaderFunction( float2 coords:TEXCOORD0, in float2 screenPos:VPOS ) : COLOR0
{
    float4 color = tex2D( s0, coords );

    float diff = ( rand( coords ) - 0.5 ) * noise;

    color.r += diff;
    color.g += diff;
    color.b += diff;

    return color;
}


technique Technique1
{
    pass Pass1
    {
        PixelShader = compile ps_3_0 PixelShaderFunction();
    }
}