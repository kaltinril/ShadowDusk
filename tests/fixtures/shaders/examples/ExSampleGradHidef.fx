// =============================================================================
// ExSampleGradHidef.fx  —  ShadowDusk fresh example fixture (Phase 33 / issue #7)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project on 2026-06-03.
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Gradient texture read (`Texture2D.SampleGrad`). DXC emits an
//              explicit-gradient sample and SPIRV-Cross the generic
//              `textureGrad()`. Since Phase 43 F7 the MojoShader rewrite lowers
//              it to the legacy `texture2DGrad()` + the guarded extension header
//              (ARB maps it to texture2DGradARB, EXT_gpu_shader4 keeps the name,
//              the `#if __VERSION__ >= 300` branch maps it back to textureGrad
//              for KNI HiDef/WebGL2, and the #else degrades to texture2D()).
//              The Phase 34 generic spelling failed Mesa's strict legacy
//              front-end on every Linux DesktopGL load.
//              (History: Phase 33 made this FAIL LOUDLY SD0210; Phase 34 lifted
//              the guard but chose the generic spelling.)
// Exercises  : MonoGameGlslRewriter Rule 6b (texture2DGrad + TexLodExtensionHeader).
// Expect     : OpenGL compile SUCCEEDS; .mgfx GLSL contains texture2DGrad(ps_s0,
//              and the guarded header; a large gradient leaves mip 0
//              (Phase34LodGradRenderTests).
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
