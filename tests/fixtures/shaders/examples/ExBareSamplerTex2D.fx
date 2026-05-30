// =============================================================================
// ExBareSamplerTex2D.fx  —  ShadowDusk fresh example fixture
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project on 2026-05-30.
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : A minimal MonoGame-conventional post-process that exercises the
//              "bare sampler + tex2D" legacy rewrite path (FxPreParser gap #2
//              Form 2 + gap #4): a `sampler s0;` with no associated texture,
//              sampled via the legacy `tex2D(s0, uv)` intrinsic. ShadowDusk
//              synthesizes `Texture2D s0_SDTexture;` and rewrites the call to
//              `s0_SDTexture.Sample(s0, uv)`.
// Exercises  : bare sampler decl, tex2D intrinsic, : COLOR return semantic,
//              SM3 PS-only technique. No free uniforms (simplest container).
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler s0;

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR
{
	float4 c = tex2D(s0, input.TexCoord) * input.Color;
	// Simple luminance so the output is visibly distinct from the input.
	float l = dot(c.rgb, float3(0.299, 0.587, 0.114));
	return float4(l, l, l, c.a);
}

technique BareSamplerExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
