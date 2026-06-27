// Issue #107 regression: a helper with a nested `if` that early-returns makes
// SPIRV-Cross emit a `do { … break; … } while(false);` one-shot loop in the GL
// GLSL. Desktop GL accepts do-while, but GLSL ES 1.00 (WebGL1 / KNI Reach) does
// not guarantee it, so the effect compiles + loads on desktop yet FAILS TO LOAD in
// WebGL. The MonoGameGlslRewriter (Rule 9) lowers each one-shot loop to a WebGL1-safe
// `for (int _i = 0; _i < 1; _i++) { … }`. This fixture pins the END-TO-END pipeline
// output (DXC -> SPIRV-Cross -> rewriter) as do-while-free on GL.
// Verbatim helper from the issue report (TestEarlyReturn).
#if OPENGL
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float TestEarlyReturn(float edge0, float edge1, float value)
{
    if (edge0 == edge1)
    {
        if (value <= edge0)
        {
            return 0.0f;
        }
    }

    return value;
}

float4 PS(float2 uv : TEXCOORD0) : COLOR0
{
    float t = TestEarlyReturn(0.5, 0.5, uv.x);
    return float4(t, t, t, 1);
}

technique T
{
    pass P
    {
        PixelShader = compile PS_SHADERMODEL PS();
    }
}
