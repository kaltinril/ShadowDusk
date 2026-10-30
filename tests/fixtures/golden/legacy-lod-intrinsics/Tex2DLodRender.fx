// tex2Dlod / tex2Dbias / tex2Dproj render fixture (the XNA spellings of explicit-LOD,
// biased and projective sampling), legacy D3D9 sampler syntax, vertex AND pixel stage.
//
// The scene (validation/SharedLegacyLod/LegacyLodScene.cs) binds Tex to a 512x512 texture
// with a full mip chain whose levels differ (red channel = 28 * level, green/blue = texel
// x/y), samples with point min/mag/mip filtering, and draws one quad over a 128x128 target,
// so the implicit LOD is exactly 2 (4 texels per pixel). The quad is split into four
// columns by uv.x:
//   0    vertex texture fetch: tex2Dlod in the VS at LOD_VS (level 4)
//   1    tex2Dlod in the PS at LOD_PS (level 1; an ignored LOD would read level 2)
//   2    tex2Dbias in the PS with BIAS (level 2 + 1 = 3)
//   3    tex2Dproj in the PS on (uv * PROJ_W, 0, PROJ_Q): uv when the divide happens
// Every column reads a level the implicit LOD would not, so a rewrite that drops the LOD,
// the bias or the divide changes that column. The validation drivers' positive controls
// override exactly one macro each (LOD_VS=0, LOD_PS=0, BIAS=0, PROJ_Q=1) and require that
// column, and only that column, to move.
//
// Reference: real mgfxc 3.8.4.1 /Profile:DirectX_11 compiles it at vs_4_0/ps_4_0
// (Tex2DLodRender.DirectX_11.mgfx beside it). mgfxc 3.8.5 has no SM6 reference for legacy
// sampler syntax (Vulkan refuses sampler_state, DirectX 12 drops the texture parameter), so
// Tex2DLodRenderModern.fx is the same shader in Texture2D/SamplerState syntax and carries
// the DirectX 12 and Vulkan goldens. See README.md.

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

texture Tex;
sampler TexSampler = sampler_state
{
    Texture = <Tex>;
    MinFilter = Point;
    MagFilter = Point;
    MipFilter = Point;
    AddressU = Wrap;
    AddressV = Wrap;
};

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
    output.Fetched = tex2Dlod(TexSampler, float4(input.TexCoord * 0.5 + 0.25, 0, LOD_VS));
    output.TexCoord = input.TexCoord;
    return output;
}

float4 MainPS(VSOut input) : COLOR0
{
    float2 uv = input.TexCoord;
    float4 lod  = tex2Dlod(TexSampler, float4(uv, 0, LOD_PS));
    float4 bias = tex2Dbias(TexSampler, float4(uv, 0, BIAS));
    float4 proj = tex2Dproj(TexSampler, float4(uv * PROJ_W, 0, PROJ_Q));
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
