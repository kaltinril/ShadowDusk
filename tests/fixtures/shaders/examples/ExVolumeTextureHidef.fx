// =============================================================================
// ExVolumeTextureHidef.fx  —  ShadowDusk fresh example fixture (Phase 34)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project on 2026-06-04.
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : GENERALITY GUARD case (non-2D sampler — 3D / volume). A Texture3D
//              read emits a `sampler3D` declaration + `texture()` from SPIRV-Cross.
//              The MojoShader rewrite models ONLY sampler2D — it would leave the
//              `sampler3D` decl un-renamed and wrongly rewrite the call to
//              `texture2D(sampler3D,…)`, invalid GLSL that fails at GL link time
//              (a SILENT break). Phase 33's parity bar requires a LOUD compile-time
//              failure instead. Phase 34 adds real Desktop+HiDef support and keeps
//              the loud guard ONLY for the KNI Reach / WebGL1 platform wall
//              (WebGL1 / OpenGL ES 2.0 has no 3D textures at all).
// Exercises  : MonoGameGlslRewriter non-2D sampler guard (ThrowIfUnsupportedSamplerType).
// Expect     : OpenGL compile currently FAILS with SD0210 (loud), never a silent
//              pass. (Phase 34 RED baseline; see PHASE34-INVESTIGATION.md.)
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture3D VolumeTexture;
SamplerState VolumeSampler;

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float3 TexCoord : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : SV_TARGET
{
	// 3D texture read — forces SPIRV-Cross to emit a sampler3D + texture(sampler3D, vec3).
	return VolumeTexture.Sample(VolumeSampler, input.TexCoord);
}

technique VolumeTextureHidefExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
