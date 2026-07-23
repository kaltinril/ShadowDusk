// =============================================================================
// CustomSpriteBatchEffect.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : MonoGame (MonoGame/MonoGame)
//   Repo       : https://github.com/MonoGame/MonoGame
//   Tag        : v3.8.5
//   Upstream   : Tests/Assets/Effects/CustomSpriteBatchEffect.fx
//   License    : Ms-PL - Copyright (C) MonoGame Foundation, Inc (see ./LICENSE, ./NOTICE.md)
//   Exercises  : TWO texture/sampler pairs at explicit registers (t0/s0, t1/s1), unnamed technique+pass.
// =============================================================================
// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

#include "Include.fxh"

DECLARE_TEXTURE(SourceTexture, 0);
DECLARE_TEXTURE(OtherTexture, 1);

float4 PS_Main(
    float4 position : SV_Position,
    float4 color : COLOR0,
    float2 uv : TEXCOORD0) : SV_TARGET0
{
    return SAMPLE_TEXTURE(SourceTexture, uv) + SAMPLE_TEXTURE(OtherTexture, uv);
}

technique
{
    pass
    {
        PixelShader = compile PS_PROFILE PS_Main();
    }
}
