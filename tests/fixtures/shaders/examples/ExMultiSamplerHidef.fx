// =============================================================================
// ExMultiSamplerHidef.fx  —  ShadowDusk fresh example fixture (Phase 33 / issue #7)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project on 2026-06-03.
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : GENERALITY PARITY case (positive). Four 2D textures/samplers — a
//              shape larger than the corpus (Dissolve has 2) — proving the
//              MojoShader sampler remap (ps_s0..ps_s3) scales beyond the corpus
//              AND that the Phase-33 #define ps_oC0 fragment-output fix + the new
//              guards do NOT over-trigger on ordinary multi-sampler 2D shaders.
//              This one MUST compile successfully for OpenGL.
// Exercises  : ps_s{k} sampler remap at k=4, single fragment output (ps_oC0),
//              no guard fires.
// Expect     : OpenGL compile SUCCEEDS; emitted GLSL has #define ps_oC0
//              gl_FragColor and ps_s0..ps_s3, no LOD/proj/grad, no non-2D sampler.
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D Tex0; SamplerState Samp0;
Texture2D Tex1; SamplerState Samp1;
Texture2D Tex2; SamplerState Samp2;
Texture2D Tex3; SamplerState Samp3;

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : SV_TARGET
{
	float4 c0 = Tex0.Sample(Samp0, input.TexCoord);
	float4 c1 = Tex1.Sample(Samp1, input.TexCoord);
	float4 c2 = Tex2.Sample(Samp2, input.TexCoord);
	float4 c3 = Tex3.Sample(Samp3, input.TexCoord);
	return (c0 + c1 + c2 + c3) * 0.25;
}

technique MultiSamplerHidefExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
