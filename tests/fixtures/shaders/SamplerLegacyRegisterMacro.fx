//-----------------------------------------------------------------------------
// SamplerLegacyRegisterMacro.fx — GitHub issue #299, shape 2 of 2.
//
// An explicit `register(sN)` on a LEGACY `sampler` declaration PINS the OpenGL
// texture unit: compiled at ps_3_0 the legacy sampler IS the combined sampler, so
// the clause names its SM3 sampler register directly (issue #189). mgfxc reads
// that clause off the PREPROCESSED source.
//
// Here both register numbers are spelled through a macro. Measured against the
// pinned mgfxc 3.8.4.1 /Profile:OpenGL: MaskA is on ps_s2 and MaskB on ps_s3,
// exactly as for the directly written `register(s2)` / `register(s3)` of
// SamplerRegisterSparse.fx. ShadowDusk used to read the clause off the raw source,
// where `register(MASK_A_REGISTER)` is not a register number, so it pinned nothing
// and compacted the pair onto ps_s0/ps_s1, putting MaskA on SpriteBatch's unit 0.
//
// Both legacy declaration forms are covered on purpose: MaskA is the bare form and
// MaskB carries a `sampler_state` block.
//
// validation/SamplerRegisterOrderGl arm "legacy-macro": BLUE sprite through
// SpriteBatch, RED MaskA and GREEN MaskB through parameters, output
// (a.r, b.g, 0, 1):
//   ps_s2/ps_s3 (mgfxc, correct) -> neither on unit 0                     -> (255, 255, 0) yellow
//   ps_s0/ps_s1 (the #299 bug)   -> MaskA overwritten by the BLUE sprite  -> (  0, 255, 0) green
// Only the red channel moves; the green channel is the control proving the harness
// bound anything at all.
//-----------------------------------------------------------------------------

#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

#define MASK_A_REGISTER s2
#define MASK_B_REGISTER s3

sampler MaskA : register(MASK_A_REGISTER);

Texture2D MaskBTexture;
sampler2D MaskB : register(MASK_B_REGISTER) = sampler_state { Texture = <MaskBTexture>; };

float4 PS(float4 position : SV_POSITION, float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
	// Declaration order, on purpose: the register VALUE is what is under test.
	float4 a = tex2D(MaskA, texCoord);
	float4 b = tex2D(MaskB, texCoord);
	return float4(a.r, b.g, 0, 1);
}

technique SamplerLegacyRegisterMacro
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL PS();
	}
}
