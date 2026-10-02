//-----------------------------------------------------------------------------
// SamplerReservationMacro.fx — GitHub issue #283, shape 2 of 2.
//
// A modern `SamplerState X : register(sN)` RESERVES OpenGL sampler register N: at
// ps_3_0 a texture and a sampler share one register namespace, so the combined
// sampler fxc synthesizes for each (texture, sampler) pair is allocated around it
// (issue #189). mgfxc decides that on the PREPROCESSED source.
//
// Here both registers are spelled through a macro. Measured against the pinned
// mgfxc 3.8.4.1 /Profile:OpenGL: s0 and s1 are reserved, so the two pairs take
// ps_s2 and ps_s3, exactly as for the directly written `: register(s0)` /
// `: register(s1)`. ShadowDusk used to read registers off the raw source, did not
// see these, and emitted ps_s0/ps_s1, putting MaskA on SpriteBatch's unit 0.
//
// validation/SamplerRegisterOrderGl arm "macro": BLUE sprite through SpriteBatch,
// RED MaskA and GREEN MaskB through parameters, output (a.r, b.g, 0, 1):
//   ps_s2/ps_s3 (mgfxc, correct) -> neither on unit 0       -> (255, 255, 0) yellow
//   ps_s0/ps_s1 (the #283 bug)   -> MaskA overwritten by the BLUE sprite -> (0, 255, 0) green
// Only the red channel moves; the green channel is the control proving the harness
// bound anything at all.
//-----------------------------------------------------------------------------

#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

#define SLOT(n) : register(n)

Texture2D MaskA;
Texture2D MaskB;
SamplerState MaskASampler SLOT(s0);
SamplerState MaskBSampler SLOT(s1);

float4 PS(float4 position : SV_POSITION, float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
	float4 a = MaskA.Sample(MaskASampler, texCoord);
	float4 b = MaskB.Sample(MaskBSampler, texCoord);
	return float4(a.r, b.g, 0, 1);
}

technique SamplerReservationMacro
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL PS();
	}
}
