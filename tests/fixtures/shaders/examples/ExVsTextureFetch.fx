// =============================================================================
// ExVsTextureFetch.fx  —  ShadowDusk fresh example fixture (Phase 43 F8)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project on 2026-06-12.
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : LOUD-FAILURE GUARD case. A VERTEX-stage texture fetch
//              (displacement-mapping shape: `Texture2D.SampleLevel` in the VS).
//              MonoGame 3.8.2's OpenGL runtime cannot bind vertex textures at
//              all — ShaderProgramCache.Link assigns texture units only for the
//              PIXEL shader's sampler records, and GraphicsDevice.OpenGL.cs has
//              no VertexTextures apply path — so ANY emitted GLSL would read
//              texture unit 0's incidental contents at runtime (silently wrong
//              output, typically black). The rewriter therefore fails loudly
//              instead of emitting it (Phase 43 F8 decision, recorded in
//              plan/PHASE-43-mgfx-writer-and-gl-uniform-fidelity.md).
// Exercises  : MonoGameGlslRewriter Rule 3 vertex-stage sampler guard.
// Expect     : OpenGL compile FAILS with SD0210 (loud), message naming the
//              vertex stage; never silently-black output.
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D HeightMap;
SamplerState HeightMapSampler;

float4x4 WorldViewProjection;

struct VsInput
{
	float4 Position : POSITION0;
	float2 TexCoord : TEXCOORD0;
};

struct VsOutput
{
	float4 Position : SV_POSITION;
	float2 TexCoord : TEXCOORD0;
};

VsOutput MainVS(VsInput input)
{
	VsOutput output;
	// Vertex texture fetch (explicit LOD — required in a vertex shader).
	float height = HeightMap.SampleLevel(HeightMapSampler, input.TexCoord, 0.0).r;
	float4 displaced = input.Position + float4(0.0, height, 0.0, 0.0);
	output.Position = mul(displaced, WorldViewProjection);
	output.TexCoord = input.TexCoord;
	return output;
}

float4 MainPS(VsOutput input) : SV_TARGET
{
	return float4(input.TexCoord, 0.0, 1.0);
}

technique VsTextureFetchExample
{
	pass P0
	{
		VertexShader = compile VS_SHADERMODEL MainVS();
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
