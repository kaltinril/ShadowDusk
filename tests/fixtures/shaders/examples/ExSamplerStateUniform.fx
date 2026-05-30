// =============================================================================
// ExSamplerStateUniform.fx  —  ShadowDusk fresh example fixture
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project on 2026-05-30.
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Exercises the "sampler_state Form 1 + free uniform" path: a
//              modern `Texture2D` bound through the legacy
//              `sampler2D S = sampler_state { Texture = <T>; };` form and a
//              free `float4` uniform parameter that must be wired into the
//              MonoGame `ps_uniforms_vec4` cbuffer and set by name.
// Exercises  : Texture2D decl, sampler_state Form 1 (explicit texture binding),
//              tex2D, a scalar/vector free uniform (cbuffer + by-name set).
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

float4 MainPS(VertexShaderOutput input) : COLOR
{
	float4 c = tex2D(SpriteTextureSampler, input.TexCoord) * input.Color;
	return c * TintColor;
}

technique SamplerStateUniformExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
