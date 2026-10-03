//-----------------------------------------------------------------------------
// SamplerLegacyInclude.fx — GitHub issue #308, shape 1 of 2.
//
// ShadowDusk rewrites legacy D3D9 sampler syntax (`sampler2D`, `sampler_state`,
// `tex2D`) into the SM4 form DXC compiles, but that rewrite used to read only the
// RAW tokens of the MAIN file. Here every legacy declaration, and the `tex2D`
// that reads MaskA, live in SamplerLegacyInclude.fxh: the main file declares the
// texture the include's sampler_state block binds, #includes the header, and reads
// MaskB through its own `tex2D`. mgfxc compiles it (it preprocesses first and
// parses second); ShadowDusk used to hand DXC the unrewritten include and fail with
// "unknown type name 'sampler2D'".
//
// The registers are measured against the pinned mgfxc 3.8.4.1 /Profile:OpenGL:
// MaskA is on ps_s2 and MaskB on ps_s3, the same as SamplerRegisterSparse.fx
// writes them directly (an explicit register on a legacy sampler PINS the unit,
// issue #189, read off the preprocessed source, issue #299).
//
// validation/SamplerRegisterOrderGl arm "legacy-include": BLUE sprite through
// SpriteBatch, RED MaskA and GREEN MaskB through parameters, output
// (a.r, b.g, 0, 1):
//   ps_s2/ps_s3 (mgfxc, correct) -> neither on unit 0                     -> (255, 255, 0) yellow
//   ps_s0/ps_s1                  -> MaskA overwritten by the BLUE sprite  -> (  0, 255, 0) green
//   compile failure (the #308 bug) -> the gate cannot even build the candidate
//-----------------------------------------------------------------------------

#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D MaskBTexture;

#include "SamplerLegacyInclude.fxh"

float4 PS(float4 position : SV_POSITION, float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
	// Declaration order, on purpose: the register VALUE is what is under test.
	float4 a = ReadMaskA(texCoord);
	float4 b = tex2D(MaskB, texCoord);
	return float4(a.r, b.g, 0, 1);
}

technique SamplerLegacyInclude
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL PS();
	}
}
