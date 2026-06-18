// =============================================================================
// ExReservedWordUniformRender.fx  —  ShadowDusk render-proof fixture (Phase 45, B10)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 45) on
//              2026-06-17. Project-owned (same license as the repository). NOT
//              derived from any third-party shader.
// Purpose    : RENDER proof for Phase-45 bug B10 (the GLSL reserved-word /
//              reflection-join bug). Its sibling ExReservedWordUniform.fx pins the
//              COMPILE + byte-identity rung; THIS one is shaped so the free uniform
//              `noise` (a GLSL reserved word) is the SOLE, EXACTLY-ASSERTABLE driver
//              of the output, so a render gate can prove `noise` binds to the right
//              register and DRIVES the pixels — not just that the bytes are stable.
//
//              On the OpenGL path SPIRV-Cross renames `noise` to `_noise` to keep the
//              emitted GLSL legal; the B10 offset bridge keeps the .mgfx parameter
//              named `noise` (recovered via its cbuffer byte offset), so the real
//              MonoGame runtime's effect.Parameters["noise"].SetValue(v) reaches the
//              correct ps_uniforms_vec4 register. If the bridge mapped `noise` to the
//              wrong register (or dropped it), this shader would render the wrong
//              colour (or fail to compile on GL with SD0012).
//
// Output     : float4(noise, noise, noise, 1) — a flat grey whose intensity IS the
//              `noise` value, so a rendered pixel equals round(noise * 255) on RGB.
//              Setting noise = 0.25 -> ~ (64,64,64); noise = 0.75 -> ~ (191,191,191).
//
// Exercises  : a free float uniform NAMED after a GLSL reserved word, USED so DXC
//              cannot strip it, PLUS the standard SpriteBatch texture+sampler so the
//              GL per-shader cbuffer/parameter join (the code path B10 lives in)
//              actually executes against a non-empty uniform layout. The sampled
//              texel is multiplied by 0 so it never perturbs the asserted output —
//              `noise` alone determines every pixel.
// Targets    : OpenGL (the path B10 changed) + DirectX_11 + FNA (all-runtime subset).
//              mgfxc/fxc accept `noise` too (MojoShader/D3D pack uniforms by index,
//              no reserved-word collision), so this same source is the golden arm.
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// The reserved-word free uniform. On OpenGL SPIRV-Cross renames this to `_noise`;
// the B10 offset bridge keeps the .mgfx parameter named `noise` so SetValue("noise")
// binds to the correct ps_uniforms_vec4 register.
float noise;

// Standard SpriteBatch texture + sampler so the effect loads through the normal
// sprite path AND the GL cbuffer/parameter join runs for a non-empty uniform layout.
Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
	Texture = <SpriteTexture>;
};

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR
{
	// Sample the texture so the sampler/cbuffer reflection join actually executes,
	// then multiply by 0 so the texel never affects the asserted output. `noise`
	// alone drives the result: the pixel is exactly (noise, noise, noise, 1).
	float4 texel = tex2D(SpriteTextureSampler, input.TexCoord);
	float4 c = texel * 0.0;
	c.rgb = noise;
	c.a   = 1.0;
	return c;
}

technique ReservedWordUniformRender
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
