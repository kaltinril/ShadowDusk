//-----------------------------------------------------------------------------
// RegisterDeadSampler.fx: an unregistered texture read through a sampler on
// register(s1), beside a sampler on register(s0) that nothing uses.
//
// DirectX 11 and 12 put the unregistered SpriteTex at t0 (texture and sampler
// registers are separate spaces, and the dead sampler is removed), which is the
// slot SpriteBatch binds its sprite to. On Vulkan texture and sampler registers
// share one binding space: ShadowDusk used to reserve the dead sampler's s0, so
// SpriteTex went to t1 and a SpriteBatch draw read an empty slot. A sampler
// named nowhere but in its declaration now reserves nothing, and SpriteTex is
// slot 0 as on DirectX.
//
// mgfxc 3.8.5 /Profile:Vulkan cannot build a working effect from this file (the
// unregistered texture is auto-numbered and its slot wraps to 224). The
// committed golden tests/fixtures/golden/Vulkan/RegisterDeadSampler.mgfx is
// mgfxc 3.8.5 /Profile:Vulkan on the DirectX-equivalent form of this file
// (SpriteTex : register(t0), SpriteSampler : register(s0), the dead sampler
// removed). Render row: validation/VsDrivenVulkan -- regslots, drawn by
// SpriteBatch alone (the effect parameter is never set).
//-----------------------------------------------------------------------------

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define PS_SHADERMODEL ps_6_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

Texture2D SpriteTex;
SamplerState SpriteSampler : register(s1);
SamplerState Unused : register(s0);

float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    return SpriteTex.Sample(SpriteSampler, uv) * color;
}

technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
