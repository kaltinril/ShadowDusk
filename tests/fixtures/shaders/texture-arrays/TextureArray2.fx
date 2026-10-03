//-----------------------------------------------------------------------------
// TextureArray2.fx — GitHub issue #324: an ARRAY of textures, `Texture2D Tex[2]`,
// read through one SamplerState, both halves on explicit registers.
//
// What the reference compiler puts in the parameter table (measured 2026-10-02,
// dotnet-mgfxc 3.8.5, the same for 1, 2 and 4 elements, with or without the
// explicit register):
//   /Profile:DirectX_12  one Object parameter `Tex` (Texture2D, no elements), one
//                        sampler record (texture slot 0, sampler slot 0) pointing
//                        at it, header maxTextureSlot 0. Elements beyond [0] are
//                        reachable only through GraphicsDevice.Textures[i].
//   /Profile:Vulkan      NO parameter, NO sampler record, NO descriptor binding:
//                        the texture can never be set and the effect cannot draw.
//   /Profile:DirectX_11  one parameter `Tex`, one record (3.8.4.1 and 3.8.5 alike).
//   /Profile:OpenGL      fails ("Sequence contains no matching element").
//
// ShadowDusk: DirectX_12 emits mgfxc's table (committed golden
// tests/fixtures/golden/DirectX_12/TextureArray2.mgfx); Vulkan refuses the shape
// loudly with SD0221 (the committed tests/fixtures/golden/Vulkan/TextureArray2.mgfx
// is mgfxc's empty-table output, kept as the evidence); OpenGL fails with SD0217.
//
// Render rows: validation/VsDrivenDx12 -- texarr (mgfxc golden vs ShadowDusk, real
// MonoGame 3.8.5 WindowsDX12, element 1 bound through GraphicsDevice.Textures[1])
// and validation/VsDrivenVulkan -- texarr (the SD0221 rejection, plus the golden's
// empty table). The explicit registers are what let the Vulkan reference compiler
// emit a loadable container for non-array shapes (VsTransformColorTexture.fx).
//-----------------------------------------------------------------------------

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define PS_SHADERMODEL ps_6_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

Texture2D Tex[2] : register(t0);
SamplerState TexSampler : register(s0);

float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    // Each element contributes half of the output, so an element the runtime never
    // binds shows as a half-dark picture rather than as nothing.
    float4 c = Tex[0].Sample(TexSampler, uv) + Tex[1].Sample(TexSampler, uv);
    return c * color / 2.0;
}

technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
