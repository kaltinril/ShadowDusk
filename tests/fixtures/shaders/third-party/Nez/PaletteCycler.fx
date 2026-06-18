// =============================================================================
// PaletteCycler.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : Nez (prime31/Nez)
//   Repo       : https://github.com/prime31/Nez
//   Commit     : 6c9d4a87ac62ce36e217cb5e4bbe36d1769dfa4c
//   Upstream   : DefaultContentSource/effects/PaletteCycler.fx
//   License    : MIT - Copyright (c) 2016 Mike (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Palette-swap via a 1-D LUT (tex1D / sampler1D).
// =============================================================================
sampler s0; // from SpriteBatch

texture _paletteTexture;
sampler1D _paletteTextureSampler = sampler_state
{
    Texture = <_paletteTexture>;
    AddressU = Wrap;
    AddressV = Wrap;
};

float _time; // time in seconds
float _cycleSpeed; // defaults to 0


float4 mainPixel( float2 coords:TEXCOORD0 ) : COLOR0
{
	// first grab the main texture pixel
	float4 baseTex = tex2D( s0, coords );
	
	// use one of the components of the grayscale color to calculate an index into the paletteTexture
	float index = baseTex.r + _time * _cycleSpeed;

	// return the mapped color from the paletteTexture
	return tex1D( _paletteTextureSampler, index );
}


technique PaletteCycler
{
	pass P0
	{
		PixelShader = compile ps_3_0 mainPixel();
	}
};