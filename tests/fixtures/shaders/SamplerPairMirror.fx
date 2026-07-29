//-----------------------------------------------------------------------------
// SamplerPairMirror.fx — Phase 51 A7 render fixture, the MIRROR shape.
//
// Two textures read through two SamplerStates with DIFFERENT filtering, sampled in the
// REVERSE of their HLSL declaration order. Two pairs, so two records, and each must
// carry the baked state of ITS OWN sampler half in SPIRV-Cross's declaration (first-use)
// order. A table that assumed declaration order would exchange the two filters.
//
// Sampled exactly between two texels of a 2x1 black/white texture: the Point-filtered
// pair snaps to one texel, the Linear-filtered pair returns the ~50% blend, so
//
//   R = Point-filtered  -> 0 or 255 (snapped)
//   G = Linear-filtered -> ~128     (blended)
//
// R != G is the claim. If both records were given one sampler's state the two channels
// would agree.
//
// WHY TWO TEXTURES AND NOT ONE. The tighter shape (ONE texture read through two
// SamplerStates -- the linear+point idiom) produces the right RECORDS but cannot be
// distinguished by a render in MonoGame 3.8.2's GL backend: it has no GL sampler
// objects, so it applies filtering with glTexParameteri on the bound TEXTURE object.
// Binding one texture to two units therefore gives both units whichever filter was
// applied last -- measured here, both channels came back equal and flipped together
// when the pair order was reversed. That is a runtime limitation that applies equally
// to mgfxc's output, not a ShadowDusk defect, and the record-level claim for that shape
// is pinned by OpenGl_OneTextureTwoSamplers_BakesEachPairsOwnSamplerState instead.
//-----------------------------------------------------------------------------

Texture2D LinearTexture;
Texture2D PointTexture;

SamplerState PointSampler
{
	MinFilter = Point;
	MagFilter = Point;
	MipFilter = Point;
	AddressU = Clamp;
	AddressV = Clamp;
};

SamplerState LinearSampler
{
	MinFilter = Linear;
	MagFilter = Linear;
	MipFilter = Linear;
	AddressU = Clamp;
	AddressV = Clamp;
};

float4 PS(float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
	// NOT named `point` / `linear` -- both are HLSL keywords (a GS primitive type and an
	// interpolation modifier), so those spellings do not parse as identifiers.
	//
	// PointTexture/PointSampler is sampled FIRST even though PointSampler is declared
	// second, so the pair order is the reverse of the HLSL declaration order. It also
	// puts the Point pair on ps_s0, whose texture unit SpriteBatch re-binds to the sprite
	// after EffectPass.Apply() -- so the harness passes PointTexture AS the sprite.
	float snapped = PointTexture.Sample(PointSampler, texCoord).r;
	float blended = LinearTexture.Sample(LinearSampler, texCoord).r;
	return float4(snapped, blended, 0, 1);
}

technique SamplerMirror
{
	pass P0
	{
		PixelShader = compile ps_3_0 PS();
	}
}
