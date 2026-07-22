// =============================================================================
// Sd0400GradientInDivergentLoop.fx  —  ShadowDusk fresh lint fixture (SD0400)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #141).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader (the loop shape mirrors the issue-#141
//              verification repro).
// Purpose    : Pin the SD0400 portability warning — the USER writes a gradient
//              op (ddx) inside a loop with a divergent exit (a conditional
//              break). ANGLE Direct3D11 (WebGL in every Windows browser)
//              silently zeroes every derivative in such a loop; fxc warns X3553
//              and force-unrolls the same HLSL, so staying silent was a fidelity
//              gap (issue #141). The compile must SUCCEED while warning SD0400.
// Exercises  : for-loop with conditional break + ddx() inside the body.
// Regression : Issue #141 (no diagnostic for user gradient-in-divergent-loop).
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
    float4 tex = tex2D(SpriteTextureSampler, input.TexCoord) * input.Color;

    // The #141 shape: a gradient op inside a loop whose body has a conditional
    // break — well-formed everywhere, silently dead on ANGLE D3D11.
    float acc = 0.0;
    for (int i = 0; i < 8; i++)
    {
        if (acc > input.TexCoord.y * 4.0)
        {
            break;
        }
        acc += abs(ddx(input.TexCoord.x)) + 0.125;
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
