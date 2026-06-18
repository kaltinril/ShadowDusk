// =============================================================================
// ExModernSamplerState.fx  —  ShadowDusk fresh example fixture (Phase 45, B2)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 45) on
//              2026-06-17. Project-owned (same license as the repository). NOT
//              derived from any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Pin Phase-45 bug B2 — a sampler declared via the FX9
//              `sampler S = sampler_state { Texture = <T>; };` form but USED
//              through the MODERN Texture method `T.Sample(S, uv)` (NOT the
//              legacy `tex2D`). Before the fix the FxPreParser erased the whole
//              declaration (S was not in _legacyIntrinsicSamplers), so DXC then
//              failed with "undeclared identifier 'S'". The declaration must now
//              survive as a passthrough `SamplerState S;` so `.Sample(S, …)`
//              resolves. This is the MonoGame HiDef SpriteEffect / modern KNI 2D
//              shape (a sampler_state initializer kept for tooling, sampled via
//              the SM4 method).
// Exercises  : Texture2D decl, sampler_state Form 1 (explicit texture binding)
//              referenced ONLY by a modern `.Sample(sampler, uv)` call, a free
//              float4 uniform.
// Targets    : OpenGL + DirectX_11. FNA is N/A — `.Sample` is SM4 method syntax
//              (the FNA fx_2_0/SM<=3 target uses the tex2D form instead), so this
//              fixture is deliberately excluded from the FNA SM3 corpus.
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4 TintColor;

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
	Texture = <SpriteTexture>;
};

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : SV_TARGET
{
	// MODERN method call on a sampler that was declared with a sampler_state
	// initializer — the B2 shape. (No tex2D anywhere in this shader.)
	float4 c = SpriteTexture.Sample(SpriteTextureSampler, input.TexCoord) * input.Color;
	return c * TintColor;
}

technique ModernSamplerStateExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
