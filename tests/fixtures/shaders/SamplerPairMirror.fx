//-----------------------------------------------------------------------------
// SamplerPairMirror.fx — Phase 51 A7 render fixture, the MIRROR shape.
//
// Two textures read through two samplers with DIFFERENT filtering, sampled in the REVERSE
// of their declaration order. Two (texture, sampler) pairs, so two sampler records, and
// each must carry the baked state of ITS OWN sampler half in SPIRV-Cross's declaration
// (first-use) order. A table that assumed declaration order would exchange the filters.
//
// Real mgfxc agrees on that ordering independently: its own OpenGL golden for this fixture
// names ps_s0 `PointSampler+PointTexture` -- the first-SAMPLED pair, not the first-declared
// one -- which is MojoShader arriving at the same first-use rule from the other side.
//
// Sampled between two texels of a 2x1 black/white texture: the Point-filtered pair snaps to
// one texel, the Linear-filtered pair returns the ~50% blend, so
//
//   R = Point-filtered  -> 0 or 255 (snapped)
//   G = Linear-filtered -> ~128     (blended)
//
// R != G is the claim. If both records were given one sampler's state the channels would agree.
//
// WHY LEGACY `sampler_state` AND NOT `SamplerState X { MinFilter = ...; }`. The modern-block
// spelling with D3D9 state names is a shape ShadowDusk bakes and mgfxc does NOT (fxc maps
// D3D10-style names like `Filter` in a `SamplerState` block, not `MinFilter`/`MagFilter`), so
// writing it that way made this fixture diverge from its own golden on baked state and turned
// the render arm into a ShadowDusk-only claim instead of an mgfxc-parity one. `sampler_state`
// is the spelling both compilers bake identically -- the same one the render-proven
// SamplerStatesFull fixture uses. It costs nothing here: the pre-parser rewrites legacy
// `sampler2D`/`tex2D` to `SamplerState` + `<texture>.Sample(...)` before DXC sees the source,
// so this still reaches the GL backend as two separate texture+sampler pairs.
//
// WHY TWO TEXTURES AND NOT ONE. The tighter shape (ONE texture through two samplers, the
// linear+point idiom) produces the right RECORDS but cannot be distinguished by a render in
// MonoGame 3.8.2's GL backend: it has no GL sampler objects and applies filtering with
// glTexParameteri on the bound TEXTURE object, so one texture bound to two units gets
// whichever filter was applied last (measured -- both channels came back equal and flipped
// together when the pair order was reversed). That is a runtime limitation applying equally to
// mgfxc's output, not a ShadowDusk defect; the record-level claim for that shape is pinned by
// OpenGl_OneTextureTwoSamplers_BakesEachPairsOwnSamplerState instead.
//-----------------------------------------------------------------------------
#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D LinearTexture;
Texture2D PointTexture;

sampler2D LinearSampler = sampler_state
{
	Texture   = <LinearTexture>;
	MinFilter = LINEAR;
	MagFilter = LINEAR;
	MipFilter = LINEAR;
	AddressU  = CLAMP;
	AddressV  = CLAMP;
};

sampler2D PointSampler = sampler_state
{
	Texture   = <PointTexture>;
	MinFilter = POINT;
	MagFilter = POINT;
	MipFilter = POINT;
	AddressU  = CLAMP;
	AddressV  = CLAMP;
};

float4 PS(float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
	// PointSampler is sampled FIRST even though it is declared second, so the pair order is
	// the reverse of the declaration order. It also puts the Point pair on ps_s0, whose
	// texture unit SpriteBatch re-binds to the sprite after EffectPass.Apply() -- so the
	// harness passes PointTexture AS the sprite rather than fighting that.
	float snapped = tex2D(PointSampler, texCoord).r;
	float blended = tex2D(LinearSampler, texCoord).r;
	return float4(snapped, blended, 0, 1);
}

technique SamplerMirror
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL PS();
	}
}
