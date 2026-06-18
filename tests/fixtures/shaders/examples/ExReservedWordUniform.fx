// =============================================================================
// ExReservedWordUniform.fx  —  ShadowDusk fresh example fixture (Phase 45, B10)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 45) on
//              2026-06-17. Project-owned (same license as the repository). NOT
//              derived from any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Pin Phase-45 bug B10 (the GLSL reserved-word / reflection-join bug,
//              a DIFFERENT class from the B1-B9 dropped-operator pre-parser bugs).
//              A FREE uniform whose name collides with a GLSL reserved word
//                  float noise;            // 'noise' clashes with GLSL's noiseN()
//              is renamed by SPIRV-Cross to '_noise' to keep the emitted GLSL
//              legal. But CompilationPipeline joined the rewriter's GLSL uniform
//              layout to the reflected effect-parameter list BY NAME, so the GLSL
//              side ('_noise') found no 'noise' on the parameter side and the loud
//              SD0012 internal-consistency guard fired. 'noise' is valid HLSL that
//              fxc/mgfxc accept (it compiles fine on DirectX and FNA). The fix is
//              an OFFSET BRIDGE: on a name miss only, the GL uniform's byte offset
//              (BaseRegister * 16) recovers the reflected variable's ORIGINAL name,
//              so the parameter resolves and stays exposed under 'noise'.
// Exercises  : a free float uniform NAMED after a GLSL reserved word, USED in the
//              pixel body (so DXC cannot strip it), plus a bare sampler + tex2D so
//              the cbuffer/parameter join actually runs. SM3 PS-only technique.
// Regression : Before the fix, the OpenGL compile of this shader failed with
//              SD0012 ("GL uniform '_noise' ... has no matching effect parameter").
//              DirectX_11 and FNA were always fine (no GLSL on those paths).
// Targets    : OpenGL + DirectX_11 + FNA (all-runtime SM3 / fx_2_0 subset).
//              After the B10 fix, effect.Parameters["noise"].SetValue(...) resolves
//              on every target.
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// The reserved-word free uniform. On the OpenGL path SPIRV-Cross renames this to
// '_noise' in the emitted GLSL; the offset bridge keeps the .mgfx parameter named
// 'noise' so the consumer's SetValue("noise", ...) still binds.
float noise;

// A bare sampler + tex2D so the pixel shader has a real texture fetch (and so the
// GL per-shader cbuffer/parameter join — the code path B10 lives in — actually
// executes for a non-empty uniform layout).
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

	// USE 'noise' in the body so DXC keeps it (a dead uniform would be stripped,
	// hiding the bug). A trivial grain-style perturbation driven by the uniform.
	float grain = frac(sin(dot(input.TexCoord, float2(12.9898, 78.233))) * 43758.5453);
	float diff  = (grain - 0.5) * noise;

	c.rgb += diff;
	return c;
}

technique ReservedWordUniformExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
