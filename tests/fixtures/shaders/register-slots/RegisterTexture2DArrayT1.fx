//-----------------------------------------------------------------------------
// RegisterTexture2DArrayT1.fx: a Texture2DArray (ONE texture with array slices,
// not an array of textures) on register(t1)/register(s1), sampling slice 1.
//
// MonoGame 3.8.4.1+ creates such a texture through the public
// Texture2D(device, w, h, mipmap, format, arraySize) constructor and fills a slice
// with SetData(level, arraySlice, rect, data, start, count), so the shape is
// render-provable. On Vulkan the slot is the register (mgfxc 3.8.5 writes slot 1,
// SPIR-V binding 33); ShadowDusk used to write slot 0 and the effect drew
// (0,0,0,0) in real DesktopVK where mgfxc's build draws slice 1.
//
// Committed golden: tests/fixtures/golden/Vulkan/RegisterTexture2DArrayT1.mgfx
// (mgfxc 3.8.5 /Profile:Vulkan). Render row: validation/VsDrivenVulkan -- regslots.
// OpenGL has no texture arrays at ps_3_0, so that target refuses the shape.
//-----------------------------------------------------------------------------

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define PS_SHADERMODEL ps_6_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

Texture2DArray Arr : register(t1);
SamplerState ArrSampler : register(s1);

float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    return Arr.Sample(ArrSampler, float3(uv, 1)) * color;
}

technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
