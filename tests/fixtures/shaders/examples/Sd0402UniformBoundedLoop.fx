// =============================================================================
// Sd0402UniformBoundedLoop.fx  —  ShadowDusk fresh lint fixture (SD0402)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #138).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the SD0402 "runtime-bounded loop" case that Rule 13
//              (MonoGameGlslRewriter.LowerBoundedHeaderlessForLoop) does NOT and
//              cannot fix: the trip count here comes straight from a uniform, with
//              no compile-time-provable ceiling anywhere in the shader (unlike
//              Apos.Shapes' Newton loop, whose "runtime" bound turned out to
//              actually be a ternary between two literals). SD0402 must keep
//              warning on this one; the compile must still SUCCEED.
// Exercises  : for-loop whose upper bound is a uniform-derived value.
// Regression : Issue #138 (loop shapes outside GLSL ES 1.00 Appendix A) — the
//              genuinely-unfixable half, kept apart from the Nez GaussianBlur.fx
//              (now fixed) and Apos.Shapes apos-shapes.fx (now also fixed) cases.
// =============================================================================
#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

float StepCount;

struct PixelInput
{
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

float4 MainPS(PixelInput input) : COLOR0
{
    float4 tex = tex2D(SpriteTextureSampler, input.TexCoord) * input.Color;

    // No literal, and no ternary-of-literals, bounds this loop anywhere in the
    // shader — StepCount is a plain runtime uniform, so there is no compile-time
    // ceiling Rule 13 (or anything else) could ever prove. The data-dependent early
    // break (mirroring the convergence check in Apos.Shapes' Newton loop) is what
    // pushes SPIRV-Cross into the divergent-exit `for (;;)` structured form instead
    // of a plain bounded for-loop.
    int steps = (int)StepCount;
    float acc = 0.0;
    for (int i = 0; i < steps; i++)
    {
        acc += abs(input.TexCoord.x - float(i) * 0.1) * 0.05;
        if (acc > 0.999)
        {
            break;
        }
    }

    return float4(tex.rgb * saturate(acc), tex.a);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
