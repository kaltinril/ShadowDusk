// =============================================================================
// ExSamplerRegisterState.fx  —  ShadowDusk fresh example fixture (Phase 45, B8)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 45) on
//              2026-06-17. Project-owned (same license as the repository). NOT
//              derived from any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Pin Phase-45 bug B8 — a sampler whose `register(sN)` clause appears
//              BEFORE the `= sampler_state` initializer:
//                  sampler S : register(s0) = sampler_state { Texture = <T>; };
//              The FxLexer drops the ':', so after the name the parser sees
//              'register' (not '='). Before the fix the dispatch required '='
//              immediately after the name, so this mis-routed to the bare-sampler
//              path and leaked the state block into the HLSL handed to DXC. The
//              dispatch now skips an optional `register ( … )` clause before the
//              '= sampler_state', and ParseSamplerDecl consumes that clause.
// Exercises  : sampler_state Form 1 with a leading register() clause + tex2D.
// Targets    : OpenGL + DirectX_11 + FNA (all-runtime SM3 / fx_2_0 subset).
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler : register(s0) = sampler_state
{
	Texture = <SpriteTexture>;
};

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color : COLOR0;
	float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR0
{
	return tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
}

technique SamplerRegisterStateExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
