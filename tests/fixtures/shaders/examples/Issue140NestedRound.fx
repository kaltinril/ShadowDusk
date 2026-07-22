// =============================================================================
// Issue140NestedRound.fx  —  ShadowDusk fresh regression fixture (issue #140)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #140).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the issue-#140 class — a round() NESTED inside another
//              round()'s argument. Rule 8 used to resume its scan past the whole
//              replacement, so the inner call survived as roundEven() in the GL
//              output: absent from GLSL ES 1.00 (WebGL1 / KNI Reach) and
//              rejected by Mesa's strict versionless-1.10 front end — a silent
//              Effect-load failure with compile exit 0. The lowering must now
//              visit calls nested inside a replaced call's argument.
// Exercises  : round(round(x) * k) nesting in the PS body, textured sampling.
// Regression : Issue #140 (nested round survives Rule 8).
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

struct PixelInput
{
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

float4 MainPS(PixelInput input) : COLOR0
{
    // The #140 shape: the inner round() sits inside the outer round()'s argument.
    float snapped = round(round(input.TexCoord.x * 7.0) * 0.5) / 4.0;
    float2 uv = float2(snapped, input.TexCoord.y);
    return tex2D(SpriteTextureSampler, uv) * input.Color;
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
