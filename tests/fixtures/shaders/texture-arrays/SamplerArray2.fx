//-----------------------------------------------------------------------------
// SamplerArray2.fx — GitHub issue #340: an ARRAY of samplers, `SamplerState
// Samplers[2]`, one per texture. An expect-diagnostic fixture with NO golden:
// the reference compiler refuses the shape before any shader compiles.
//
// mgfxc 3.8.4.1 and 3.8.5, EVERY profile (DirectX_11, OpenGL, DirectX_12, Vulkan),
// measured 2026-10-02, pointing at the `[` of the declaration below:
//   SamplerArray2.fx(31,22) : Unexpected token '[' found. Expected Semicolon,
//   Comma, or CloseParenthesis.
//   Failed to parse 'SamplerArray2.fx'!
//
// ShadowDusk: DirectX_11 and DirectX_12 refuse it with SD0224 at the declaration
// (31,14). Before the fix both compiled it: DirectX 11 to two records, t0/s0 and
// t1/s1, DirectX 12 to one record for slot 0. Vulkan refuses it with SD0221
// (issue #324); OpenGL fails in SPIRV-Cross (SD0100: arrays of separate samplers
// cannot be remapped to plain GLSL), as mgfxc itself cannot build it there either.
//
// Render row: validation/VsDrivenDx -- samparr asserts the DirectX_11 refusal.
//-----------------------------------------------------------------------------

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define PS_SHADERMODEL ps_6_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

Texture2D TexA : register(t0);
Texture2D TexB : register(t1);
SamplerState Samplers[2];

float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
    return (TexA.Sample(Samplers[0], uv) + TexB.Sample(Samplers[1], uv)) * color / 2.0;
}

technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
