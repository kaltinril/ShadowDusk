// =============================================================================
// Bevels.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : MonoGame (MonoGame/MonoGame)
//   Repo       : https://github.com/MonoGame/MonoGame
//   Tag        : v3.8.5
//   Upstream   : Tests/Assets/Effects/Bevels.fx
//   License    : Ms-PL - Copyright (C) MonoGame Foundation, Inc (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Neighbor-tap emboss through the Include.fxh macro layer (DECLARE_TEXTURE/SAMPLE_TEXTURE).
// =============================================================================
// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

#include "Include.fxh"

DECLARE_TEXTURE(s, 0);

float4 PixelShaderFunction( float4 inPosition : SV_Position,
			    float4 inColor : COLOR0,
			    float2 coords : TEXCOORD0) : SV_TARGET0
{
    float4 color = SAMPLE_TEXTURE(s, coords);
    
    color -= SAMPLE_TEXTURE(s, coords - 0.002) * 2.5f;
    color += SAMPLE_TEXTURE(s, coords + 0.002) * 2.5f;

    return color;
}

technique Technique1
{
    pass Pass1
    {
        PixelShader = compile PS_PROFILE PixelShaderFunction();
    }
}
