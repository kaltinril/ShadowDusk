//-----------------------------------------------------------------------------
// SamplerReservationIfBranch.fx — GitHub issue #283, shape 1 of 2.
//
// A modern `SamplerState X : register(sN)` RESERVES OpenGL sampler register N: at
// ps_3_0 a texture and a sampler share one register namespace, so the combined
// sampler fxc synthesizes for the (texture, sampler) pair is allocated around it
// (issue #189). mgfxc decides that on the PREPROCESSED source.
//
// Here the register exists ONLY in the branch OpenGL does not compile. Measured
// against the pinned mgfxc 3.8.4.1 /Profile:OpenGL: ps_s0, so the pair sits on
// SpriteBatch's texture unit 0 and samples the sprite. ShadowDusk used to read the
// register off the raw source, counted the dead branch, and emitted ps_s1: the
// texture landed on unit 1, where the sprite is not.
//
// validation/SamplerRegisterOrderGl arm "ifbranch" draws a RED sprite through
// SpriteBatch and binds a GREEN texture through the SpriteTexture parameter:
//   ps_s0 (mgfxc, correct)  -> unit 0, overwritten by the sprite -> (255,   0, 0) red
//   ps_s1 (the #283 bug)    -> unit 1, the parameter's texture   -> (  0, 255, 0) green
//-----------------------------------------------------------------------------

#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;

#if OPENGL
SamplerState SpriteSampler;
#else
SamplerState SpriteSampler : register(s0);
#endif

float4 PS(float4 position : SV_POSITION, float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
	return SpriteTexture.Sample(SpriteSampler, texCoord);
}

technique SamplerReservationIfBranch
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL PS();
	}
}
