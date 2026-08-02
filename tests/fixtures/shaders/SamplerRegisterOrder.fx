//-----------------------------------------------------------------------------
// SamplerRegisterOrder.fx — GitHub issue #189 render fixture.
//
// The canonical MonoGame SpriteBatch custom-effect shape: `register(s0)` IS the
// SpriteBatch texture (SpriteBatcher.FlushVertexArray does `_device.Textures[0] =
// texture;` right AFTER EffectPass.Apply(), so unit 0 always ends up holding the
// sprite), and `register(s1)` is the effect's own second texture, set through an
// effect parameter.
//
// The one thing that makes this fixture a test rather than a demo: the two samplers
// are SAMPLED IN THE REVERSE of their DECLARATION order. fxc — and therefore mgfxc —
// allocates OpenGL sampler slots in DECLARATION order, honouring `register(sN)`, so
// its build puts SpriteSampler on unit 0 and MaskSampler on unit 1. ShadowDusk used
// to allocate them in FIRST-USE order (SPIRV-Cross's combined-sampler numbering),
// which put MaskSampler on unit 0 — where SpriteBatch then overwrote it with the
// sprite — and SpriteSampler on unit 1, reading a texture nothing had bound.
//
// Driven with a RED sprite texture and a GREEN mask, output = (sprite.r, mask.g, 0, 1):
//
//   correct (declaration order) -> (255, 255,   0)  yellow
//   first-use order (the bug)   -> (  0,   0,   0)  black
//
// Both channels flip together, which is what makes the two outcomes impossible to
// confuse with a tolerance artefact. A symmetric expression like `sprite * mask`
// would render identically under the swap and could not prove anything.
//
// NOTE the deliberate contrast with SharedSamplerPair.fx / SamplerPairMirror.fx
// (Phase 51 A7): those fixtures give both textures IDENTICAL pixels precisely so the
// ps_s{k} numbering cannot affect the picture. That is why the A7 gates were green
// while this defect shipped. This fixture is the counterexample they were built not
// to see.
//-----------------------------------------------------------------------------

// Dual-profile like the rest of the corpus, so the same fixture yields real mgfxc
// goldens for OpenGL AND the DirectX family.
#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// Legacy `sampler … : register(sN)` on purpose: it is the form the issue reports, the
// form every SpriteBatch custom effect in the wild uses, and the form whose register
// clause FxPreParser used to drop when synthesizing the SM4 Texture2D + SamplerState.
sampler SpriteSampler : register(s0);
sampler MaskSampler   : register(s1);

float4 PS(float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
	// Reverse of declaration order — see the header.
	float4 mask   = tex2D(MaskSampler,   texCoord);
	float4 sprite = tex2D(SpriteSampler, texCoord);
	return float4(sprite.r, mask.g, 0, 1);
}

technique SamplerRegisterOrder
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL PS();
	}
}
