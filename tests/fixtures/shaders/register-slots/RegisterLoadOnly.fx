//-----------------------------------------------------------------------------
// RegisterLoadOnly.fx: a texture on register(t1) that is only ever LOADED (no
// sampler), beside an ordinary sampled pair on t0/s0 (SpriteBatch's sprite).
//
// A texture with no sampler is a SAMPLED_IMAGE descriptor. mgfxc 3.8.5
// /Profile:Vulkan writes it a sampler record of its own (texture slot 1,
// sampler slot 255, empty name, no state) and its build draws the texture in
// real MonoGame DesktopVK. ShadowDusk wrote no record for it, so the parameter's
// texture was never bound and the Load read (0,0,0,0).
//
// Committed golden: tests/fixtures/golden/Vulkan/RegisterLoadOnly.mgfx (mgfxc
// 3.8.5 /Profile:Vulkan on this file). Render row: validation/VsDrivenVulkan --
// regslots. OpenGL (ps_3_0) has no Load and refuses the shape.
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

float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    // Half the sprite, half the loaded texel, so an unbound Tex1 shows as a dark picture.
    return (SpriteTex.Sample(SpriteSampler, uv) + Tex1.Load(int3(0, 0, 0))) * color / 2.0;
}

technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
