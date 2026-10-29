//-----------------------------------------------------------------------------
// RegisterGapT0T2.fx: two textures on explicit registers with a GAP between them,
// t0/s0 and t2/s2, nothing at t1.
//
// Base (t0) is the texture SpriteBatch draws (it owns device slot 0); Overlay (t2)
// is set through its effect parameter. Each contributes half of the output, so a
// texture the runtime never binds shows as a half-dark picture, not as nothing.
//
// The second texture's slot is 2 on every target (DirectX 11, DirectX 12, and
// mgfxc 3.8.5 /Profile:Vulkan, where t2 is SPIR-V binding 34). ShadowDusk's Vulkan
// output used to write slot 1 (the ordinal), which left device slot 2 unbound.
//
// Committed golden: tests/fixtures/golden/Vulkan/RegisterGapT0T2.mgfx (mgfxc 3.8.5
// /Profile:Vulkan). Render row: validation/VsDrivenVulkan -- regslots.
//-----------------------------------------------------------------------------

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define PS_SHADERMODEL ps_6_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

Texture2D Base : register(t0);
SamplerState BaseSampler : register(s0);
Texture2D Overlay : register(t2);
SamplerState OverlaySampler : register(s2);

float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    float4 c = Base.Sample(BaseSampler, uv) + Overlay.Sample(OverlaySampler, uv);
    return c * color / 2.0;
}

technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
