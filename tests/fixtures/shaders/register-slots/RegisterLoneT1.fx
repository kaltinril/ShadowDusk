//-----------------------------------------------------------------------------
// RegisterLoneT1.fx: a single texture + sampler pinned to register(t1)/register(s1),
// with NOTHING at t0/s0.
//
// The explicit register is the texture's slot on every target: DirectX 11 and
// DirectX 12 bind it at slot 1, and mgfxc 3.8.5 /Profile:Vulkan writes texture slot
// 1 too (its slot is the SPIR-V binding minus the 32 that -fvk-t-shift adds; t1 is
// binding 33). ShadowDusk's Vulkan output used to renumber the slot to 0 (the
// texture's ORDINAL among the stage's textures), so the effect parameter's texture
// went to device slot 0 while the descriptor read slot 1: in real MonoGame 3.8.5
// DesktopVK the effect drew (0,0,0,0) where mgfxc's build draws the texture.
//
// Committed golden: tests/fixtures/golden/Vulkan/RegisterLoneT1.mgfx (mgfxc 3.8.5
// /Profile:Vulkan). Render row: validation/VsDrivenVulkan -- regslots.
//-----------------------------------------------------------------------------

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define PS_SHADERMODEL ps_6_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

Texture2D Tex1 : register(t1);
SamplerState Tex1Sampler : register(s1);

float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    return Tex1.Sample(Tex1Sampler, uv) * color;
}

technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
