// =============================================================================
// CustomSpriteBatchEffectComparisonSampler.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : MonoGame (MonoGame/MonoGame)
//   Repo       : https://github.com/MonoGame/MonoGame
//   Tag        : v3.8.5
//   Upstream   : Tests/Assets/Effects/CustomSpriteBatchEffectComparisonSampler.fx
//   License    : Ms-PL - Copyright (C) MonoGame Foundation, Inc (see ./LICENSE, ./NOTICE.md)
//   Exercises  : SamplerComparisonState + SampleCmpLevelZero - a comparison sampler probe.
// =============================================================================
// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

#include "Include.fxh"

Texture2D<float4> SourceTexture : register(t0);

SamplerComparisonState SourceTextureSampler : register(s0);

struct VSOutput
{
    float4 position : SV_Position;
    float4 color    : COLOR0;
    float2 uv       : TEXCOORD0;
};

float4 PS_Main(VSOutput input) : SV_TARGET0
{
    float comparisonResult = SourceTexture.SampleCmpLevelZero(SourceTextureSampler, input.uv, 0.5f);
    return float4(comparisonResult, 0, 0, 1);
}

technique
{
    pass
    {
        PixelShader = compile PS_PROFILE PS_Main();
    }
}
