//-----------------------------------------------------------------------------
// RegisterMismatchedTS.fx: a texture and the sampler it is read through carry
// DIFFERENT explicit registers, register(t1) and register(s3).
//
// DirectX 11 and DirectX 12 bind the texture at slot 1 and the sampler at slot 3.
// On Vulkan, MonoGame's descriptor model is one COMBINED_IMAGE_SAMPLER per
// texture, so ShadowDusk's Vulkan binding rewrite moves the sampler onto the
// texture's register (s1) and the pair lands on one descriptor at SPIR-V binding
// 33: the texture register wins, and the slot written to the effect is 1, the
// texture's register. (mgfxc 3.8.5 /Profile:Vulkan keeps the two registers apart,
// binding 33 for the image and 35 for the sampler, a separate SAMPLED_IMAGE +
// SAMPLER pair MonoGame's native Vulkan descriptor writer does not handle.)
//
// Record-level coverage only (VulkanExplicitRegisterSlotTests); no render row,
// because the reference compiler's own output has no working Vulkan shape here.
//-----------------------------------------------------------------------------

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define PS_SHADERMODEL ps_6_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

Texture2D Tex : register(t1);
SamplerState TexSampler : register(s3);

float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    return Tex.Sample(TexSampler, uv) * color;
}

technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
