// =============================================================================
// ExModernSample.fx  —  ShadowDusk fresh example fixture
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project on 2026-05-30.
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : The control / negative case. Already-modern HLSL using the SM4+
//              object model: separate `Texture2D` + `SamplerState` and the
//              `.Sample(sampler, uv)` method, with an `SV_TARGET` return. None
//              of the legacy rewrites (texture/sampler_state/tex2D/: COLOR)
//              should fire — this proves the FxPreParser leaves modern shaders
//              untouched while still producing a loadable .mgfx.
// Exercises  : modern Texture2D + SamplerState + .Sample(), SV_TARGET return,
//              free uniform — the pass-through path (no rewrite).
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
SamplerState SpriteTextureSampler;

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : SV_TARGET
{
	float4 c = SpriteTexture.Sample(SpriteTextureSampler, input.TexCoord) * input.Color;
	return c * TintColor;
}

technique ModernSampleExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
