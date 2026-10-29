// =============================================================================
// NewSyntax_s0s1t0t1.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : KNI (kniEngine/kni)
//   Repo       : https://github.com/kniEngine/kni
//   Commit     : 1dda67cf2fc7c0cc68ea87ebc905f6ebb5d99468
//   Upstream   : Tests/Assets/Effects/NewSyntax_s0s1t0t1.fx
//   License    : Ms-PL - Copyright (c) 2014-2025 Nick Kastellanos; Copyright (c) 2009-2021 The MonoGame Team (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Texture2D.Sample with POINT/CLAMP sampler_state - NewSyntaxEffectPassShouldSetTexturesAndSamplers.
// =============================================================================

Texture2D Texture0;
Texture2D Texture1;

sampler Sampler0 = sampler_state
{
    MAGFILTER = POINT;
    MINFILTER = POINT;
    MIPFILTER = POINT;
};

sampler Sampler1 = sampler_state
{
    MAGFILTER = POINT;
    MINFILTER = POINT;
    MIPFILTER = POINT;
    AddressU = CLAMP;
    AddressV = CLAMP;
};

float4 PS_Main(float2 uv : TEXCOORD0) : COLOR0
{
    return Texture0.Sample(Sampler0, uv) * Texture1.Sample(Sampler1, uv);
}

technique
{
    pass
    {
        PixelShader = compile ps_2_0 PS_Main();
    }
}