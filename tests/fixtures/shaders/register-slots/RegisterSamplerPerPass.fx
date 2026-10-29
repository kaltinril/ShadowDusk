//-----------------------------------------------------------------------------
// RegisterSamplerPerPass.fx: ONE texture read through a DIFFERENT sampler in
// each of two techniques (point in one, linear in the other).
//
// Each compiled shader is its own Vulkan descriptor layout, so each technique's
// pixel shader is one combined descriptor at slot 0, where SpriteBatch binds
// its sprite. ShadowDusk's Vulkan binding rewrite used to pair the texture once
// for the whole file: the second technique's sampler got an ALIAS of the
// texture on slot 1, so that technique, drawn through SpriteBatch alone, read
// a slot nothing binds (only Effect.Parameters fills an alias). The rewrite now
// runs per compiled shader.
//
// mgfxc 3.8.5 /Profile:Vulkan builds this file with the second shader's sampler
// as a standalone SAMPLER descriptor and no image (a shape MonoGame's descriptor
// writer does not handle). The committed golden
// tests/fixtures/golden/Vulkan/RegisterSamplerPerPass.mgfx is mgfxc 3.8.5 on
// the same file with both techniques reading through PointSampler (same slots,
// and with no sampler state block the runtime samples through
// GraphicsDevice.SamplerStates[0] either way). Render row:
// validation/VsDrivenVulkan -- regslots (both techniques, SpriteBatch only: the cat, and
// the cat with its channels swapped).
//-----------------------------------------------------------------------------

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define PS_SHADERMODEL ps_6_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

Texture2D SpriteTex : register(t0);
SamplerState PointSampler : register(s0);
SamplerState LinearSampler : register(s1);

float4 PointPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    return SpriteTex.Sample(PointSampler, uv) * color;
}

float4 LinearPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    // Channels swapped, so the two pixel shaders differ (mgfxc merges identical ones) and each
    // technique's render is told apart from the other's.
    return SpriteTex.Sample(LinearSampler, uv).bgra * color;
}

technique Point { pass P { PixelShader = compile PS_SHADERMODEL PointPS(); } }
technique Linear { pass P { PixelShader = compile PS_SHADERMODEL LinearPS(); } }
