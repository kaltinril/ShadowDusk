// The SAME shader as Tex2DLodRender.fx, written in Texture2D / SamplerState syntax with the
// intrinsics spelled as the methods D3D defines them as:
//   tex2Dlod(s, t)  == T.SampleLevel(s, t.xy, t.w)
//   tex2Dbias(s, t) == T.SampleBias(s, t.xy, t.w)
//   tex2Dproj(s, t) == T.Sample(s, t.xy / t.w)
// It exists because mgfxc 3.8.5 has no SM6 reference for the legacy spelling (/Profile:Vulkan
// refuses sampler_state, /Profile:DirectX_12 drops the texture parameter), so its DirectX 12 and
// Vulkan builds of THIS file are the reference the legacy fixture's ShadowDusk build is diffed
// against on those targets. The equivalence itself is measured on the reference compiler:
// mgfxc 3.8.4.1's DirectX_11 builds of the two files render pixel-identically
// (validation/DxModernFeatures, arm "legacy-lod").
//
// Explicit registers: mgfxc 3.8.5's Vulkan output computes a slot as (binding - 32), so an
// auto-numbered resource underflows and the effect cannot load (the MonoGame SlotOffset bug,
// see validation/VsDrivenVulkan). No baked sampler state: the scene sets PointWrap on both
// stages, which is what the legacy fixture's sampler_state block bakes.

#if SM6
#define VS_SHADERMODEL vs_6_0
#define PS_SHADERMODEL ps_6_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

#ifndef LOD_VS
#define LOD_VS 4
#endif
#ifndef LOD_PS
#define LOD_PS 1
#endif
#ifndef BIAS
#define BIAS 1
#endif
#ifndef PROJ_W
#define PROJ_W 2
#endif
#ifndef PROJ_Q
#define PROJ_Q PROJ_W
#endif

Texture2D Tex : register(t0);
SamplerState TexSampler : register(s0);

struct VSIn
{
    float4 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
};

struct VSOut
{
    float4 Position : SV_POSITION;
    float4 Fetched  : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

VSOut MainVS(VSIn input)
{
    VSOut output;
    output.Position = float4(input.Position.xy, 0, 1);
    float4 c = float4(input.TexCoord * 0.5 + 0.25, 0, LOD_VS);
    output.Fetched = Tex.SampleLevel(TexSampler, c.xy, c.w);
    output.TexCoord = input.TexCoord;
    return output;
}

float4 MainPS(VSOut input) : SV_Target0
{
    float2 uv = input.TexCoord;
    float4 l = float4(uv, 0, LOD_PS);
    float4 b = float4(uv, 0, BIAS);
    float4 p = float4(uv * PROJ_W, 0, PROJ_Q);
    float4 lod  = Tex.SampleLevel(TexSampler, l.xy, l.w);
    float4 bias = Tex.SampleBias(TexSampler, b.xy, b.w);
    float4 proj = Tex.Sample(TexSampler, p.xy / p.w);
    return uv.x < 0.25 ? input.Fetched : (uv.x < 0.5 ? lod : (uv.x < 0.75 ? bias : proj));
}

technique LegacyLod
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
