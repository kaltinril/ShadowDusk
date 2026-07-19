// Issue #136 regression: a helper that BOTH early-returns AND takes a derivative.
// SPIRV-Cross nests the inlined helper's one-shot `do { … } while(false);` wrapper
// inside the entry point's own wrapper. On ANGLE's D3D11 backend (WebGL in every
// Windows browser) ANY loop with a divergent exit (conditional break or discard)
// silently zeroes every gradient op in its body — so if either wrapper survives as
// a loop (the old Rule-9 for-loop lowering, or the 9b fallback), the fwidth below
// reads 0.0 at runtime with no compile or link error, and derivative-based AA dies.
// The MonoGameGlslRewriter must unwrap BOTH wrappers (Rule 9a recursing through the
// plain block the outer unwrap leaves behind) so no gradient op is lexically inside
// any loop with a divergent exit. Pinned end-to-end by
// EarlyReturnHelperGradient_NoGradientInsideDivergentLoop_Issue136.
#if OPENGL
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float AaWidth(float d)
{
    if (d > 100.0f)
    {
        return 0.0f;      // early return inside the helper -> nested one-shot wrapper
    }
    return fwidth(d);     // gradient op inside the helper body
}

float4 PS(float2 uv : TEXCOORD0) : COLOR0
{
    if (uv.x > 0.99f)
    {
        return float4(0, 0, 0, 0);   // forces the entry-point one-shot wrapper
    }
    float a = AaWidth(uv.y * 30.0f);
    return float4(a, 0, 0, 1);
}

technique T
{
    pass P
    {
        PixelShader = compile PS_SHADERMODEL PS();
    }
}
