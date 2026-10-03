// =============================================================================
// XnaFiddle-Mask.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : XnaFiddle (vchelaru/XnaFiddle)
//   Repo       : https://github.com/vchelaru/XnaFiddle
//   Commit     : 0a6edd690db3a8bd42b92676c8e5b31cf13969da
//   Upstream   : XnaFiddle.BlazorGL/wwwroot/examples/Masking/Mask.fx
//   License    : MIT - Copyright (c) 2026 Victor Chelaru (see ./LICENSE, ./NOTICE.md)
//   Exercises  : two textures (sprite + mask) at the interpolated UV, multiplies by input.Color
// =============================================================================
#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#elif defined(SM6) || defined(VULKAN)
	// MonoGame's native Content Builder (DX12/Vulkan) compiles via DXC, which requires Shader
	// Model 6 and rejects the classic sampler2D/tex2D combo below -- see the Texture2D/SamplerState
	// branch and the SM6 sampling call sites further down.
	#define VS_SHADERMODEL vs_6_0
	#define PS_SHADERMODEL ps_6_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// TWO textures sampled in ONE pass. SpriteBatch binds the sprite you Draw() to slot 0
// (SpriteTexture); the mask is a SECOND texture you set from C# as the MaskTexture
// parameter. See Masking.cs for the C# side.
#if defined(SM6) || defined(VULKAN)
Texture2D<float4> SpriteTexture : register(t0);                 // slot 0 — the image SpriteBatch is drawing
SamplerState ImageSampler : register(s0);

Texture2D<float4> MaskTexture : register(t1);                    // the mask, set from C#: effect.Parameters["MaskTexture"]
SamplerState MaskSampler : register(s1);
#else
Texture2D SpriteTexture;                 // slot 0 — the image SpriteBatch is drawing
sampler2D ImageSampler = sampler_state { Texture = <SpriteTexture>; };

Texture2D MaskTexture;                    // the mask, set from C#: effect.Parameters["MaskTexture"]
sampler2D MaskSampler = sampler_state { Texture = <MaskTexture>; };
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
    float4 col = SpriteTexture.Sample(ImageSampler, input.TextureCoordinates) * input.Color;

    // The mask is sampled with the SAME UVs as the sprite, so it stretches to map 1:1
    // over the image regardless of either texture's pixel size. We read the red channel
    // (the mask is grayscale): white (1) keeps the pixel, black (0) hides it, and the
    // soft edge in between gives partial transparency.
    //
    // This lowers ONLY alpha (straight, non-premultiplied alpha), so the C# side must draw
    // with BlendState.NonPremultiplied — see the note in Masking.cs for why AlphaBlend
    // would darken instead of mask.
    float mask = MaskTexture.Sample(MaskSampler, input.TextureCoordinates).r;
    col.a *= mask;

    return col;
}
#else
float4 MainPS(VertexShaderOutput input) : COLOR
{
    float4 col = tex2D(ImageSampler, input.TextureCoordinates) * input.Color;

    // The mask is sampled with the SAME UVs as the sprite, so it stretches to map 1:1
    // over the image regardless of either texture's pixel size. We read the red channel
    // (the mask is grayscale): white (1) keeps the pixel, black (0) hides it, and the
    // soft edge in between gives partial transparency.
    //
    // This lowers ONLY alpha (straight, non-premultiplied alpha), so the C# side must draw
    // with BlendState.NonPremultiplied — see the note in Masking.cs for why AlphaBlend
    // would darken instead of mask.
    float mask = tex2D(MaskSampler, input.TextureCoordinates).r;
    col.a *= mask;

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
