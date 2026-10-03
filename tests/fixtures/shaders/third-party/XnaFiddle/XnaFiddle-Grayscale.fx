// =============================================================================
// XnaFiddle-Grayscale.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : XnaFiddle (vchelaru/XnaFiddle)
//   Repo       : https://github.com/vchelaru/XnaFiddle
//   Commit     : 0a6edd690db3a8bd42b92676c8e5b31cf13969da
//   Upstream   : XnaFiddle.BlazorGL/wwwroot/examples/Grayscale/Grayscale.fx
//   License    : MIT - Copyright (c) 2026 Victor Chelaru (see ./LICENSE, ./NOTICE.md)
//   Exercises  : channel-average grayscale, multiplies by input.Color
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#elif defined(SM6) || defined(VULKAN)
	// MonoGame's native Content Builder (DX12/Vulkan) compiles via DXC, which requires Shader
	// Model 6 and rejects the classic sampler2D/tex2D combo below -- see the Texture2D/SamplerState
	// branch and the SM6 sampling call site further down.
	#define VS_SHADERMODEL vs_6_0
	#define PS_SHADERMODEL ps_6_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

#if defined(SM6) || defined(VULKAN)
Texture2D<float4> SpriteTexture : register(t0);
SamplerState SpriteTextureSampler : register(s0);
#else
Texture2D SpriteTexture;

sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};
#endif

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

// ShadowDusk's OpenGL translator (and the real MonoGame Content Builder's DXC pass) rewrite/validate
// the pixel shader entry point by matching a literal, unconditioned `: COLOR`/`tex2D(...)` shape --
// splitting the signature or a call site across an #if breaks that (verified against the real
// ShadowDuskCLI tool and a real DX12 Content Builder build while fixing issue #52's SM6 gap: SM6
// also requires `SV_Target0` instead of `COLOR`). So the two shader models get fully separate,
// self-contained entry points instead of one shared function with an internal #if.
#if defined(SM6) || defined(VULKAN)
float4 MainPS(VertexShaderOutput input) : SV_Target0
{
    float4 col = SpriteTexture.Sample(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
    col.rgb = (col.r + col.g + col.b) / 3.0f;
    return col;
}
#else
float4 MainPS(VertexShaderOutput input) : COLOR
{
    float4 col = tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
    col.rgb = (col.r + col.g + col.b) / 3.0f;
    return col;
}
#endif

technique BasicColorDrawing
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
