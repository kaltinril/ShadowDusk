// =============================================================================
// SpriteBlinkEffect.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : Nez (prime31/Nez)
//   Repo       : https://github.com/prime31/Nez
//   Commit     : 6c9d4a87ac62ce36e217cb5e4bbe36d1769dfa4c
//   Upstream   : DefaultContentSource/effects/SpriteBlinkEffect.fx
//   License    : MIT - Copyright (c) 2016 Mike (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Sprite blink tint via lerp by a uniform-alpha; minimal VS-output-struct PS.
// =============================================================================
sampler s0;
float4 _blinkColor; // 1,1,1,1


struct VertexShaderOutput
{
	float4 Position : POSITION;
	float4 Color : COLOR0;
	float2 TextureCoordinates : TEXCOORD0;
};


float4 mainPixel( VertexShaderOutput input ) : COLOR
{
	float4 color = tex2D( s0, input.TextureCoordinates ) * input.Color;
	color.rgb = lerp( color.rgb, _blinkColor.rgb, _blinkColor.a );
	
	return color;
}


technique SpriteBlink
{
	pass P0
	{
		PixelShader = compile ps_3_0 mainPixel();
	}
};