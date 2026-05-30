// =============================================================================
// ExDualTexture.fx  —  ShadowDusk fresh example fixture
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project on 2026-05-30.
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Exercises MULTIPLE sampler bindings in one effect — the case
//              that historically exposed the cbuffer-interleave / per-sampler
//              binding bugs. Two textures, each bound through its own
//              sampler_state, each sampled with tex2D, blended by a uniform.
// Exercises  : two Texture2D decls, two sampler_state Form 1 decls, two tex2D
//              calls resolving to distinct textures, a float blend uniform.
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float BlendAmount;

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state { Texture = <SpriteTexture>; };

Texture2D OverlayTexture;
sampler2D OverlayTextureSampler = sampler_state { Texture = <OverlayTexture>; };

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR
{
	float4 baseColor    = tex2D(SpriteTextureSampler,  input.TexCoord) * input.Color;
	float4 overlayColor = tex2D(OverlayTextureSampler, input.TexCoord);
	return lerp(baseColor, overlayColor, saturate(BlendAmount));
}

technique DualTextureExample
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
