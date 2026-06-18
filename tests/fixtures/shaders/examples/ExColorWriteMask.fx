// =============================================================================
// ExColorWriteMask.fx  —  ShadowDusk fresh example fixture (Phase 45, B3)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 45) on
//              2026-06-17. Project-owned (same license as the repository). NOT
//              derived from any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Pin Phase-45 bug B3 — the canonical D3D9/XNA color-write mask
//              `ColorWriteEnable = Red | Green | Blue;` in a pass render-state
//              block. The FxLexer drops '|', so the value tokenizes to three
//              adjacent identifiers (Red Green Blue); before the fix the pass
//              render-state parser read exactly ONE token then demanded ';',
//              failing with FX0008 on 'Green'. (The bare `ColorWriteEnable = Red;`
//              form ALSO failed because RenderStateParser used int.TryParse on
//              the symbolic flag — now it uses TryParseColorWriteMask.) Render
//              states are stripped on every profile, so this compiles on all
//              three targets.
// Exercises  : ColorWriteEnable OR'd flag mask in a pass; SpriteBatch PS shape.
// Targets    : OpenGL + DirectX_11 + FNA (all-runtime SM3 / fx_2_0 subset).
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
	Texture = <SpriteTexture>;
};

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color : COLOR0;
	float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR0
{
	return tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
}

technique ColorWriteMaskExample
{
	pass P0
	{
		// The B3 shape: an OR of color-write flags whose '|' the lexer drops.
		ColorWriteEnable = Red | Green | Blue;
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
