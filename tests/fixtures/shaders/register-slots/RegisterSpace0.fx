//-----------------------------------------------------------------------------
// RegisterSpace0.fx: a texture, its sampler and a constant buffer, each with an
// explicit register SPACE, register(t1, space0) / register(s1, space0) /
// register(b0, space0), in a pixel shader.
//
// A register space is a Vulkan descriptor set. MonoGame's DesktopVK binds a
// pixel shader's resources from set 1 only (set 0 is the vertex stage's), so a
// literal space0 parks all three where the runtime never writes. mgfxc 3.8.5
// /Profile:Vulkan keeps the space (DescriptorSet 0 in its SPIR-V) and its build,
// like ShadowDusk's before 2026-10-28, loads into real DesktopVK and draws
// (0,0,0,0). ShadowDusk drops the space on Vulkan (one set per stage), so the
// resources land in set 1 and the effect draws the texture times Tint.
//
// The committed golden tests/fixtures/golden/Vulkan/RegisterSpace0.mgfx is
// mgfxc 3.8.5 /Profile:Vulkan on THIS file with every ", space0" removed: the
// reference compiler's build of the shader as MonoGame can run it, since its
// build of the file as written draws nothing. Render row:
// validation/VsDrivenVulkan -- regslots.
//-----------------------------------------------------------------------------

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define PS_SHADERMODEL ps_6_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

Texture2D Tex1 : register(t1, space0);
SamplerState Tex1Sampler : register(s1, space0);

cbuffer TintBuffer : register(b0, space0)
{
    float4 Tint;
};

float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    return Tex1.Sample(Tex1Sampler, uv) * Tint * color;
}

technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
