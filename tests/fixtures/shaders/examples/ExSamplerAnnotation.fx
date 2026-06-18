// =============================================================================
// ExSamplerAnnotation.fx  —  ShadowDusk fresh example fixture (Phase 45, B9)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 45) on
//              2026-06-17. Project-owned (same license as the repository). NOT
//              derived from any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Pin Phase-45 bug B9 — a sampler-level FX annotation block trailing
//              the sampler_state's closing '}':
//                  sampler2D S = sampler_state { … } < string UIName = "x"; >;
//              Before the fix, ParseSamplerDecl hard-required ';' right after the
//              '}', failing with FX0001 on the '<'. The parser now optionally
//              consumes a trailing annotation block before the required ';'. The
//              annotation is FX metadata (stripped on the MGFX targets, valid FX
//              syntax for vkd3d on FNA) and does not affect the SamplerInfo.
// Exercises  : sampler_state Form 1 + a trailing `< … >` annotation block + tex2D.
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
sampler2D SpriteTextureSampler = sampler_state
{
	Texture = <SpriteTexture>;
} < string UIName = "Diffuse"; int UIOrder = 1; >;

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

technique SamplerAnnotationExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
