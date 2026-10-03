//-----------------------------------------------------------------------------
// SamplerReservationKeywords.fx — GitHub issue #309.
//
// On OpenGL a sampler declared with an explicit `register(sN)` RESERVES unit N:
// the combined sampler fxc synthesizes for every (texture, sampler) pair read
// through `Texture.Sample` is allocated AROUND it (issue #189). ShadowDusk's
// reservation matcher only recognised the exact keyword `SamplerState`. fxc
// reserves for EVERY sampler type keyword, used or not:
//   - `sampler MaskSampler : register(s0)` (the lowercase keyword, read through
//     .Sample by both textures below) reserves s0;
//   - `SamplerComparisonState Unused : register(s1)` (never read) reserves s1.
//
// Measured against the pinned mgfxc 3.8.4.1 /Profile:OpenGL: MaskATexture lands
// on ps_s2 and MaskBTexture on ps_s3. ShadowDusk used to put them on ps_s0/ps_s1,
// MaskA on the unit SpriteBatch binds.
//
// validation/SamplerRegisterOrderGl arm "keyword-reservation": BLUE sprite through
// SpriteBatch, RED MaskATexture and GREEN MaskBTexture through parameters, output
// (a.r, b.g, 0, 1):
//   ps_s2/ps_s3 (mgfxc, correct) -> neither on unit 0                     -> (255, 255, 0) yellow
//   ps_s0/ps_s1 (the #309 bug)   -> MaskA overwritten by the BLUE sprite  -> (  0, 255, 0) green
// Only the red channel moves; the green channel is the control proving the harness
// bound anything at all.
//-----------------------------------------------------------------------------

#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D MaskATexture;
Texture2D MaskBTexture;

sampler MaskSampler : register(s0);
SamplerComparisonState Unused : register(s1);

float4 PS(float4 position : SV_POSITION, float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
	float4 a = MaskATexture.Sample(MaskSampler, texCoord);
	float4 b = MaskBTexture.Sample(MaskSampler, texCoord);
	return float4(a.r, b.g, 0, 1);
}

technique SamplerReservationKeywords
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL PS();
	}
}
