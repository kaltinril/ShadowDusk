// =============================================================================
// ExPhantomTexLodUniform.fx  —  ShadowDusk fresh regression fixture (#187)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #187).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Pin the TEXLOD-PROLOGUE member of the issue-#187 phantom class.
//              The shader's only numeric uniform folds away entirely on the
//              -spirv leg (forcing the synthesized-declaration INSERT path) AND
//              it samples with an explicit LOD (`SampleLevel`, the
//              ExSampleLevelHidef shape — cross-platform DXC syntax, unlike the
//              Windows-only tex2Dlod intrinsic), so the emitted GL fragment
//              source leads with the TexLod header — a BALANCED
//              `#if __VERSION__ >= 300 … #elif … #extension
//              GL_ARB_shader_texture_lod : enable … #endif` block whose
//              #extension directives live INSIDE branches. The synthesized
//              `uniform vec4 ps_uniforms_vec4[N];` must land AFTER that whole
//              block: Mesa desktop GL hard-errors on a mid-shader #extension
//              (and Mesa takes the GL_ARB_shader_texture_lod branch) — the
//              defect class the final audit caught one prologue block over
//              from the derivatives header.
// Exercises  : SampleLevel + a fully-folded uniform in the same fragment shader
//              (GlPrologueEnd's balanced-#if block consumption).
// Regression : Issue #187 (phantom-parameter backing, TexLod-prologue insert).
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float3 GhostScale;

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
	// (x * s) / s cancels to x on the -spirv leg — GhostScale vanishes from
	// the shipped GLSL — while the explicit-LOD sample survives and forces the
	// TexLod #if/#elif/#endif extension header to lead the source.
	float2 uv = (input.TexCoord * GhostScale.xy) / GhostScale.xy;
	return SpriteTexture.SampleLevel(SpriteTextureSampler, uv, 0.0) * input.Color;
}

technique PhantomTexLod
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
