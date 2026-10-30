//-----------------------------------------------------------------------------
// RegisterTwoSamplers.fx: ONE texture sampled through TWO samplers (the linear +
// point idiom), beside SpriteBatch's sprite pair.
//
// MonoGame's DesktopVK binds a texture and its sampler as one combined
// descriptor, so one texture read through two samplers needs two descriptors
// holding the same image. ShadowDusk used to put Tex1 with its first sampler and
// leave the second as a standalone SAMPLER descriptor (a shape MonoGame's
// descriptor writer does not handle). It now declares an alias of Tex1 on its
// own register for the second sampler and binds that record to the Tex1
// parameter, so setting Tex1 fills both slots. (SpriteBatch fills slot 0 only, so
// a texture used this way must be set through its effect parameter.)
//
// mgfxc 3.8.5 /Profile:Vulkan has the same standalone-sampler problem. The
// committed golden tests/fixtures/golden/Vulkan/RegisterTwoSamplers.mgfx is
// mgfxc 3.8.5 on the two-texture form (Tex1 : register(t1) with LinearSampler,
// Tex1b : register(t2) with PointSampler, both set to the same texture by the
// render row). Render row: validation/VsDrivenVulkan -- regslots.
//-----------------------------------------------------------------------------

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define PS_SHADERMODEL ps_6_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

Texture2D SpriteTex : register(t0);
SamplerState SpriteSampler : register(s0);
Texture2D Tex1 : register(t1);
SamplerState LinearSampler : register(s1);
SamplerState PointSampler : register(s2);

float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    // A third each, so a slot left unbound shows as a darker picture.
    float4 c = SpriteTex.Sample(SpriteSampler, uv) + Tex1.Sample(LinearSampler, uv) + Tex1.Sample(PointSampler, uv);
    return c * color / 3.0;
}

technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
