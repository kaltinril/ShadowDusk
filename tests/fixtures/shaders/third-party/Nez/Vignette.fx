// =============================================================================
// Vignette.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : Nez (prime31/Nez)
//   Repo       : https://github.com/prime31/Nez
//   Commit     : 6c9d4a87ac62ce36e217cb5e4bbe36d1769dfa4c
//   Upstream   : DefaultContentSource/effects/Vignette.fx
//   License    : MIT - Copyright (c) 2016 Mike (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Radial vignette post-FX: dot-based falloff + swizzle.
// =============================================================================
sampler s0;

float _power; // 1.0
float _radius; // 1.25


float4 mainPS( float2 texCoord:TEXCOORD0 ) : COLOR0
{
	float4 color = tex2D( s0, texCoord );
	float2 dist = ( texCoord - 0.5f ) * _radius;
	dist.x = 1 - dot( dist, dist ) * _power;
	color.rgb *= dist.x;

	return color;
}



technique Vignette
{
	pass P0
	{
		PixelShader = compile ps_3_0 mainPS();
	}
};