// =============================================================================
// ExSampleLevelHidef.fx  —  ShadowDusk fresh example fixture (Phase 33 / issue #7)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project on 2026-06-03.
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : GENERALITY GUARD case. An explicit-LOD texture read
//              (`Texture2D.SampleLevel`) compiles fine through DXC and emits
//              `textureLod()` from SPIRV-Cross, which the MojoShader rewrite
//              would turn into `texture2DLod()` — a builtin that is NOT a valid
//              GLSL ES 1.00 fragment builtin (KNI Reach/WebGL1) and that KNI's
//              HiDef/WebGL2 ES-3.00 converter does NOT rewrite. There is no
//              single-blob GLSL form valid in both profiles, so the rewriter
//              must FAIL LOUDLY (ShaderError SD0210) rather than silently emit
//              GLSL that breaks under KNI HiDef. (Phase 34 = HiDef-safe emission.)
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
	// Explicit mip level — forces SPIRV-Cross to emit textureLod().
	return SpriteTexture.SampleLevel(SpriteTextureSampler, input.TexCoord, 2.0);
}

technique SampleLevelHidefExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
