//-----------------------------------------------------------------------------
// SharedSamplerPair.fx — Phase 51 A7 render fixture.
//
// Two textures read through ONE shared SamplerState: the classic diffuse+lightmap
// shape, ordinary HLSL that mgfxc compiles (its own golden for this shape,
// tests/fixtures/golden/OpenGL/PenumbraTexture.mgfx, carries TWO sampler records).
// ShadowDusk rejected it with SD0216 until the GL sampler table was keyed on
// (texture, sampler) PAIRS instead of on the reflected samplers.
//
// The output is deliberately ASYMMETRIC in the two textures so a render can tell the
// failure modes apart. Driven through SpriteBatch with a RED sprite texture (which
// MonoGame forces onto texture unit 0) and a GREEN Lightmap:
//
//   correct                        -> (255, 255,   0)  yellow
//   ps_s1 never assigned a unit    -> (255,   0,   0)  red    (both read unit 0)
//   the two pairs swapped          -> (  0,   0,   0)  black
//
// A symmetric expression like `diffuse * light` would render identically under a
// swap, so it could not prove the binding at all.
//-----------------------------------------------------------------------------

Texture2D DiffuseMap;
Texture2D Lightmap;
SamplerState TextureSampler;

float4 PS(float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
	float4 diffuse = DiffuseMap.Sample(TextureSampler, texCoord);
	float4 light   = Lightmap.Sample(TextureSampler, texCoord);
	return float4(diffuse.r, light.g, 0, 1);
}

technique SharedSampler
{
	pass P0
	{
		PixelShader = compile ps_3_0 PS();
	}
}
