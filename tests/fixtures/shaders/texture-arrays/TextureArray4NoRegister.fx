//-----------------------------------------------------------------------------
// TextureArray4NoRegister.fx — GitHub issue #324, the other axis of the shape:
// a 4-element `Texture2D Tex[4]` and its SamplerState with NO explicit register.
//
// mgfxc reflects it exactly as TextureArray2.fx (see that file's header):
// DirectX_12 one parameter `Tex` bound to slot 0; Vulkan nothing at all (both
// 3.8.5); DirectX_11 one parameter `Tex`, one record t0/s0 (3.8.4.1, the pinned v10
// oracle, and 3.8.5). The committed goldens under tests/fixtures/golden/
// {DirectX_11,DirectX_12,Vulkan}/ pin all three. ShadowDusk: DirectX_12 and
// DirectX_11 match (issue #339 for DirectX_11); Vulkan refuses with SD0221 naming
// `Tex` and the element count 4.
//-----------------------------------------------------------------------------

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define PS_SHADERMODEL ps_6_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

Texture2D Tex[4];
SamplerState TexSampler;

float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    float4 c = Tex[0].Sample(TexSampler, uv) + Tex[1].Sample(TexSampler, uv)
             + Tex[2].Sample(TexSampler, uv) + Tex[3].Sample(TexSampler, uv);
    return c * color / 4.0;
}

technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
