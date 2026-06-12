// =============================================================================
// ExSampleLevelHidef.fx  —  ShadowDusk fresh example fixture (Phase 33 / issue #7)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project on 2026-06-03.
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Explicit-LOD texture read (`Texture2D.SampleLevel`). DXC emits an
//              OpImageSampleExplicitLod and SPIRV-Cross the generic `textureLod()`.
//              Since Phase 43 F7 the MojoShader rewrite lowers it to the legacy
//              `texture2DLod()` + the guarded extension header (MojoShader's
//              prepend_glsl_texlod_extensions): valid on Mesa's strict legacy
//              front-end (the generic form is GLSL>=1.30-only and failed every
//              Linux DesktopGL load), mapped back to `textureLod` by the header's
//              `#if __VERSION__ >= 300` branch under KNI HiDef/WebGL2, and
//              degraded gracefully to texture2D() where no extension exists.
//              (History: Phase 33 made this FAIL LOUDLY SD0210; Phase 34 lifted
//              the guard but chose the generic spelling, which Mesa rejects.)
// Exercises  : MonoGameGlslRewriter Rule 6b (texture2DLod + TexLodExtensionHeader).
// Expect     : OpenGL compile SUCCEEDS; .mgfx GLSL contains texture2DLod(ps_s0,
//              and the guarded header; render honors the explicit mip
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
