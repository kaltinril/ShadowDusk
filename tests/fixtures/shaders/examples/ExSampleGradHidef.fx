// =============================================================================
// ExSampleGradHidef.fx  —  ShadowDusk fresh example fixture (Phase 33 / issue #7)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project on 2026-06-03.
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : GENERALITY GUARD case. A gradient texture read
//              (`Texture2D.SampleGrad`) compiles through DXC and emits
//              `textureGrad()` from SPIRV-Cross — an ES-3.00 / GL-3.30-only
//              builtin that is invalid in KNI Reach/WebGL1 (GLSL ES 1.00) AND is
//              NOT rewritten by KNI's HiDef/WebGL2 converter. No single-blob form
//              serves both profiles, so the rewriter must FAIL LOUDLY (SD0210).
// Exercises  : MonoGameGlslRewriter LOD/proj/grad guard (ThrowIfUnsupportedSampling).
// Expect     : OpenGL compile FAILS with SD0210 (loud), never a silent pass.
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

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
	// Explicit gradients — forces SPIRV-Cross to emit textureGrad().
	return SpriteTexture.SampleGrad(SpriteTextureSampler, input.TexCoord,
		float2(0.01, 0.0), float2(0.0, 0.01));
}

technique SampleGradHidefExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
