//-----------------------------------------------------------------------------
// SamplerLegacyMacroDecl.fx — GitHub issue #308, shape 2 of 2.
//
// The legacy sampler declarations and the `tex2D` read come out of MACROS, the
// way MonoGame's own Macros.fxh / Include.fxh spell them for the DX9 branch:
//   DECLARE_TEXTURE(Name, index)  -> sampler2D Name : register(s##index)
//   SAMPLE_TEXTURE(Name, texCoord) -> tex2D(Name, texCoord)
// plus a register clause that is a function-like macro, `sampler MaskB SLOT(s3)`.
// None of these is a sampler declaration or a tex2D call in the RAW token stream,
// so ShadowDusk's SM4 rewrite could not see them and DXC rejected the expansion.
// mgfxc compiles all three (it preprocesses first and parses second).
//
// Measured against the pinned mgfxc 3.8.4.1 /Profile:OpenGL: MaskA is on ps_s2
// and MaskB on ps_s3, exactly as the directly written `register(s2)` /
// `register(s3)` of SamplerRegisterSparse.fx.
//
// validation/SamplerRegisterOrderGl arm "legacy-macro-decl": BLUE sprite through
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

#define DECLARE_TEXTURE(Name, index) \
	sampler2D Name : register(s##index)
#define SAMPLE_TEXTURE(Name, texCoord) tex2D(Name, texCoord)
#define SLOT(n) : register(n)

DECLARE_TEXTURE(MaskA, 2);

Texture2D MaskBTexture;
sampler MaskB SLOT(s3) = sampler_state { Texture = <MaskBTexture>; };

float4 PS(float4 position : SV_POSITION, float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
	// Declaration order, on purpose: the register VALUE is what is under test.
	float4 a = SAMPLE_TEXTURE(MaskA, texCoord);
	float4 b = tex2D(MaskB, texCoord);
	return float4(a.r, b.g, 0, 1);
}

technique SamplerLegacyMacroDecl
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL PS();
	}
}
