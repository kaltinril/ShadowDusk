// Phase 28 — VS-driven MonoGame effect fixture.
//
// A custom vertex shader that takes a float4x4 transform and the SpriteBatch-
// compatible vertex set (POSITION0 / COLOR0 / TEXCOORD0), plus a textured +
// tinted pixel shader. This exercises the full VS-side MonoGame-GL contract:
//   - a mat4 free-uniform (WorldViewProjection) expanded to 4 vs_uniforms_vec4
//     registers,
//   - the legacy attribute table (vs_v0/vs_v1/vs_v2 -> Position/Color/TexCoord),
//   - VS outputs carried over the varyings the PS reads (vFrontColor/vTexCoord0),
//   - gl_Position written from SV_Position.
//
// It deliberately uses TRUE SV_Position (not the legacy `#define SV_POSITION
// POSITION` form) so DXC emits a real gl_Position output the rewriter can lower;
// the older POSITION-aliased form produces a dead user-varying on the DXC path.

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define VS_SHADERMODEL vs_6_0
#define PS_SHADERMODEL ps_6_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4x4 WorldViewProjection;
float4   Tint;

// Vulkan compiles at SM6 through DXC, which dropped the FX9 sampler_state/tex2D forms.
// ShadowDusk's pre-parser CAN convert them, but the reference compiler (mgfxc) cannot —
// so the modern branch is written out explicitly here, exactly as the PS-only corpus
// fixtures do. That is what lets BOTH compilers build this shader for Vulkan, which is
// what makes a reference-compiler pixel A/B possible on this target (issue #145).
//
// The MATCHING EXPLICIT REGISTERS are load-bearing for the reference side: mgfxc 3.8.5
// computes a Vulkan texture slot as (rawBinding - 32) and only -fvk-t-shift/-fvk-s-shift
// shifted (i.e. explicitly annotated) resources come out ≥ 32. An auto-numbered pair
// underflows to 224/225 and its own container is then unloadable — the upstream bug that
// blocked a Vulkan baseline render in Phase 32. With explicit registers, mgfxc's output
// loads and renders, so this fixture can be diffed against it.
#if SM6
Texture2D    SpriteTexture        : register(t0);
SamplerState SpriteTextureSampler : register(s0);
#else
Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};
#endif

struct VertexShaderInput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

struct VertexShaderOutput
{
    float4 Position : SV_Position;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

VertexShaderOutput MainVS(VertexShaderInput input)
{
    VertexShaderOutput output = (VertexShaderOutput)0;
    output.Position = mul(input.Position, WorldViewProjection);
    output.Color    = input.Color * Tint;
    output.TexCoord = input.TexCoord;
    return output;
}

#if SM6
float4 MainPS(VertexShaderOutput input) : SV_Target0
{
    return SpriteTexture.Sample(SpriteTextureSampler, input.TexCoord) * input.Color;
}
#else
float4 MainPS(VertexShaderOutput input) : SV_Target0
{
    return tex2D(SpriteTextureSampler, input.TexCoord) * input.Color;
}
#endif

technique SpriteDrawing
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
