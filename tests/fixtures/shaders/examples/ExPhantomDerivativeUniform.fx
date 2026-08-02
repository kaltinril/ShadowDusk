// =============================================================================
// ExPhantomDerivativeUniform.fx  —  ShadowDusk fresh regression fixture (#187)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #187).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the PROLOGUE-INTERACTION member of the issue-#187 phantom
//              class. The shader's only uniform folds away entirely on the
//              -spirv leg (so the synthesized `uniform vec4 ps_uniforms_vec4[N];`
//              declaration must be INSERTED, none exists to resize) AND the
//              shader uses derivatives, so the emitted GL fragment source leads
//              with `#extension GL_OES_standard_derivatives : enable` (issue
//              #139) followed by the `#ifdef GL_ES` precision block. The
//              synthesized declaration must land AFTER that whole prologue:
//              GLSL ES requires #extension before any non-preprocessor token,
//              and a float-typed global before the ES default-precision
//              statement is rejected by strict ESSL compilers (ANGLE/WebGL,
//              Android GLES) — desktop GL is lenient, which is exactly how such
//              a bug would slip past desktop render gates.
// Exercises  : fwidth() + a fully-folded uniform in the same fragment shader.
// Regression : Issue #187 (phantom-parameter backing, declaration insertion).
// =============================================================================
#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float3 GhostResolution;

struct PixelInput
{
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

float4 MainPS(PixelInput input) : COLOR0
{
    // (x * r) / r cancels to x on the -spirv leg — GhostResolution vanishes
    // from the shipped GLSL — while the derivative use survives and forces the
    // GL_OES_standard_derivatives extension header to lead the source.
    float2 uv = (input.TexCoord * GhostResolution.xy) / GhostResolution.xy;
    float w = fwidth(uv.x);
    return float4(uv, w, 1.0);
}

technique PhantomDerivative
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
