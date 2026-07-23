// =============================================================================
// TextureArrayEffect.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : MonoGame (MonoGame/MonoGame)
//   Repo       : https://github.com/MonoGame/MonoGame
//   Tag        : v3.8.5
//   Upstream   : Tests/Assets/Effects/TextureArrayEffect.fx
//   License    : Ms-PL - Copyright (C) MonoGame Foundation, Inc (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Texture2DArray sampling - an array-texture probe.
// =============================================================================
// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.


#if SM6

#define PS_PROFILE ps_6_0
#define VS_PROFILE vs_6_0

#elif SM4

#define PS_PROFILE ps_4_0
#define VS_PROFILE vs_4_0

#else

#define PS_PROFILE ps_3_0
#define VS_PROFILE vs_3_0

#endif

matrix WorldViewProj;

Texture2DArray Texture : register(t0);
SamplerState TextureSampler : register(s0);

struct VSOutput
{
    float4 PositionPS : SV_Position;
    float2 TexCoord : TEXCOORD0;
};

struct PSInput
{
    float4 PositionSS : SV_Position;
    float2 TexCoord : TEXCOORD0;
};

VSOutput VS_Main(uint VertexID : SV_VertexID)
{
    VSOutput output;
    output.TexCoord = float2((VertexID << 1) & 2, VertexID & 2);
    output.PositionPS = float4(output.TexCoord * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0f, 1.0f);
    return output;
}

float4 PS_Main(PSInput input) : SV_TARGET0
{
    return Texture.Sample(TextureSampler, float3(input.TexCoord, (uint) input.PositionSS.x % 4));
}

technique
{
    pass
    {
        VertexShader = compile VS_PROFILE VS_Main();
        PixelShader = compile PS_PROFILE PS_Main();
    }
}
