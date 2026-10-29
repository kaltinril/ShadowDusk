//-----------------------------------------------------------------------------
// RegisterHelperPair.fx: textures paired with their samplers THROUGH HELPER
// FUNCTIONS, no explicit registers. SpriteTex is sampled directly; Overlay is
// sampled by Blend(), which takes the texture and sampler as parameters, and
// Tint1 by Tinted(), which takes the texture and reads a GLOBAL sampler.
//
// MonoGame's DesktopVK binds a texture and its sampler as ONE combined
// descriptor, so the Vulkan binding rewrite must know each pair. It used to see
// only direct Tex.Sample(Sampler, ...) calls: Overlay and Tint1 and their samplers
// got separate registers, and the effect shipped a standalone SAMPLER descriptor
// next to a SAMPLED_IMAGE (a shape MonoGame's descriptor writer does not handle);
// the textures never reached the shader. The rewrite now follows helper calls.
//
// mgfxc 3.8.5 /Profile:Vulkan cannot build a working effect from this file (no
// explicit registers: its slots wrap to 224+). The committed golden
// tests/fixtures/golden/Vulkan/RegisterHelperPair.mgfx is mgfxc 3.8.5 on the
// explicit-register form (SpriteTex/SpriteSampler t0/s0, Overlay/OverlaySampler
// t1/s1, Tint1/TintSampler t2/s2). Render row: validation/VsDrivenVulkan -- regslots.
//-----------------------------------------------------------------------------

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define PS_SHADERMODEL ps_6_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

Texture2D SpriteTex;
SamplerState SpriteSampler;
Texture2D Overlay;
SamplerState OverlaySampler;
Texture2D Tint1;
SamplerState TintSampler;

float4 Blend(Texture2D t, SamplerState s, float2 uv)
{
    return t.Sample(s, uv);
}

float4 Tinted(Texture2D t, float2 uv)
{
    return t.Sample(TintSampler, uv);
}

float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    // A third each, so any texture left unbound shows as a darker picture.
    float4 c = SpriteTex.Sample(SpriteSampler, uv) + Blend(Overlay, OverlaySampler, uv) + Tinted(Tint1, uv);
    return c * color / 3.0;
}

technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
