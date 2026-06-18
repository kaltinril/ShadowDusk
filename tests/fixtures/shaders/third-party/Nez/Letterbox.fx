// =============================================================================
// Letterbox.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : Nez (prime31/Nez)
//   Repo       : https://github.com/prime31/Nez
//   Commit     : 6c9d4a87ac62ce36e217cb5e4bbe36d1769dfa4c
//   Upstream   : DefaultContentSource/effects/Letterbox.fx
//   License    : MIT - Copyright (c) 2016 Mike (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Letterbox bars via VPOS screen-space + min() + relational if.
// =============================================================================
sampler s0;

float4 _color; // 0,0,0,1
float _letterboxSize; // 0


float4 mainPS( float2 texCoord:TEXCOORD0, in float2 screenPos:VPOS ) : COLOR0
{
	float4 color = tex2D( s0, texCoord );

	// get the position from the bottom of the screen in pixels. we can use the screenPos along with the texCoord to calculate this since we are full screen
	// in a post processor.
	float positionFromBottom = screenPos.y / texCoord.y - screenPos.y;

	// we want to show the letterbox whenever we are at the top or bottom of the screen within _letterboxSize
	if( min( screenPos.y, positionFromBottom ) < _letterboxSize )
		color = _color;

	return color;
}



technique Vignette
{
	pass P0
	{
		PixelShader = compile ps_3_0 mainPS();
	}
}