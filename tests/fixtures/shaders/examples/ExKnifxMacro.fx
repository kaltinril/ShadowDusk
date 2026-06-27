// Phase 35 regression: KNI's effect compiler (MojoEffectProcessor) always defines
// __KNIFX__=1, and KNI's own Macros.fxh plus real KNI shaders (e.g. Apos.Shapes, used by
// Gum) branch on it. ShadowDusk targets KNI via the KNIFX container, so a KNIFX-targeted
// compile must define __KNIFX__ (and the default/universal MGFX output must NOT).
// This shader makes the branch observable: the KNIFX arm writes a distinct constant the
// emitted GLSL/HLSL carries, so a test can assert which branch was taken.
#if OPENGL
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler s0;

float4 PS(float2 uv : TEXCOORD0) : COLOR0
{
#ifdef __KNIFX__
    return float4(1, 0, 0, 1);   // KNIFX branch (KNI compiler defines __KNIFX__)
#else
    return float4(0, 1, 0, 1);   // non-KNIFX branch (MonoGame / universal MGFX)
#endif
}

technique T
{
    pass P
    {
        PixelShader = compile PS_SHADERMODEL PS();
    }
}
