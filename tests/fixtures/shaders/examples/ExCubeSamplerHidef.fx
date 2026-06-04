// =============================================================================
// ExCubeSamplerHidef.fx  —  ShadowDusk fresh example fixture (Phase 33 / issue #7)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project on 2026-06-03.
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : GENERALITY GUARD case (non-2D sampler). A TextureCube read emits a
//              `samplerCube` declaration + `texture()` from SPIRV-Cross. The
//              MojoShader rewrite models ONLY sampler2D — it would leave the
//              `samplerCube` decl un-renamed and wrongly rewrite the call to
//              `texture2D(samplerCube,…)`, invalid GLSL that fails at GL link
//              time (a SILENT break). Phase 33's parity bar requires a LOUD
//              compile-time failure instead. Cube-map support itself is out of
//              scope (Phase 34); the only obligation here is to fail loudly.
// Exercises  : MonoGameGlslRewriter non-2D sampler guard (ThrowIfUnsupportedSamplerType).
// Expect     : OpenGL compile FAILS with SD0210 (loud), never a silent pass.
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

TextureCube EnvMap;
SamplerState EnvSampler;

struct VertexShaderOutput
{
	float4 Position  : SV_POSITION;
	float4 Color     : COLOR0;
	float3 Direction : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : SV_TARGET
{
	return EnvMap.Sample(EnvSampler, input.Direction);
}

technique CubeSamplerHidefExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
