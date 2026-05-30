// =============================================================================
// ExLegacyTextureDiscard.fx  —  ShadowDusk fresh example fixture
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project on 2026-05-30.
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : A clean, fully-owned analogue of the Dissolve gap #3 case. It
//              uses the legacy effect-framework `texture T;` object type plus a
//              `sampler S = sampler_state { Texture = <T>; };` form bound to it,
//              and a `clip()`/discard driven by a scalar uniform. ShadowDusk
//              rewrites `texture T;` -> `Texture2D T;` (gap #3) so the resource
//              the sampler_state references exists for DXC.
// Exercises  : legacy `texture` decl rewrite (gap #3), sampler_state Form 1
//              bound to a legacy texture, second sampler, clip()/discard,
//              scalar uniform threshold.
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float Cutoff; // 0..1; pixels whose mask.r is below this are discarded

sampler s0; // the sprite being drawn

texture _maskTex;
sampler _maskSampler = sampler_state { Texture = <_maskTex>; };

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR
{
	float4 color = tex2D(s0, input.TexCoord) * input.Color;
	float  mask  = tex2D(_maskSampler, input.TexCoord).r;

	// Discard pixels under the cutoff — a hard-edged reveal/erase.
	clip(mask - Cutoff);

	return color;
}

technique LegacyTextureDiscardExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
