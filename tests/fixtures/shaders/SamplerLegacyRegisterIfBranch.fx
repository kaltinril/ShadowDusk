//-----------------------------------------------------------------------------
// SamplerLegacyRegisterIfBranch.fx — GitHub issue #299, shape 1 of 2.
//
// An explicit `register(sN)` on a LEGACY `sampler` declaration PINS the OpenGL
// texture unit: compiled at ps_3_0 the legacy sampler IS the combined sampler, so
// the clause names its SM3 sampler register directly (issue #189). mgfxc reads
// that clause off the PREPROCESSED source.
//
// Here both registers exist ONLY in the branch OpenGL does not compile, and they
// are deliberately the REVERSE of declaration order. Measured against the pinned
// mgfxc 3.8.4.1 /Profile:OpenGL: nothing is pinned, so the samplers are allocated
// in declaration order, SpriteSampler on ps_s0 and MaskSampler on ps_s1.
// ShadowDusk used to read the registers off the raw source, counted the dead
// branch, and pinned SpriteSampler to unit 1 and MaskSampler to unit 0.
//
// Both legacy declaration forms are covered on purpose: SpriteSampler carries a
// `sampler_state` block (the register sits between the name and the `=`), and
// MaskSampler is the bare form.
//
// validation/SamplerRegisterOrderGl arm "legacy-ifbranch" draws a RED sprite
// through SpriteBatch (which owns unit 0), binds a BLUE texture through the
// SpriteTexture parameter and a GREEN one through the mask parameter, and reads
// (sprite.r, mask.g, 0, 1):
//   ps_s0/ps_s1 (mgfxc, correct) -> SpriteSampler reads the RED sprite, MaskSampler
//                                   the GREEN mask          -> (255, 255, 0) yellow
//   swapped (the #299 bug)       -> SpriteSampler reads the BLUE parameter texture,
//                                   MaskSampler the RED sprite -> (0, 0, 0) black
// Both channels flip together, so neither outcome can be a tolerance artefact.
//-----------------------------------------------------------------------------

#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;

#if OPENGL
sampler SpriteSampler = sampler_state { Texture = <SpriteTexture>; };
sampler MaskSampler;
#else
sampler SpriteSampler : register(s1) = sampler_state { Texture = <SpriteTexture>; };
sampler MaskSampler : register(s0);
#endif

float4 PS(float4 position : SV_POSITION, float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
	float4 sprite = tex2D(SpriteSampler, texCoord);
	float4 mask = tex2D(MaskSampler, texCoord);
	return float4(sprite.r, mask.g, 0, 1);
}

technique SamplerLegacyRegisterIfBranch
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL PS();
	}
}
