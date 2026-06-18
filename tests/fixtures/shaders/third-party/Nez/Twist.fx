// =============================================================================
// Twist.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : Nez (prime31/Nez)
//   Repo       : https://github.com/prime31/Nez
//   Commit     : 6c9d4a87ac62ce36e217cb5e4bbe36d1769dfa4c
//   Upstream   : DefaultContentSource/effects/Twist.fx
//   License    : MIT - Copyright (c) 2016 Mike (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Swirl/twist distortion: relational if(dist<radius) + length/sin/cos UV warp.
// =============================================================================
sampler s0;

float radius; // 0.5
float angle; // 5.0
float2 offset; // 0.5, 0.5


float4 PixelShaderFunction( float2 texCoord:TEXCOORD0 ) : COLOR0
{
    float2 coord = texCoord - offset;
    float dist = length( coord );

    if( dist < radius )
    {
        float ratio = ( radius - dist ) / radius;
        float angleMod = ratio * ratio * angle;
        float s = sin( angleMod );
        float c = cos( angleMod );
        coord = float2( coord.x * c - coord.y * s, coord.x * s + coord.y * c );
    }

    return tex2D( s0, coord + offset );
}


technique Technique1
{
    pass Pass1
    {
        PixelShader = compile ps_3_0 PixelShaderFunction();
    }
}