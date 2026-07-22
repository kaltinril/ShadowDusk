// =============================================================================
// Issue139DerivativeExtension.fx  —  ShadowDusk fresh regression fixture (#139)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #139).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the issue-#139 class — a fragment shader using derivative
//              intrinsics (including fwidth, which SPIRV-Cross emits directly
//              and mgfxc's two-token dFdx/dFdy scan never had to handle) must
//              ship with `#extension GL_OES_standard_derivatives : enable` as
//              the FIRST line of the emitted GL fragment source, exactly where
//              mgfxc puts it (ShaderData.mojo.cs). Without the header, strict
//              ESSL 1.00 compilers (native GLES 2.0) reject the shader at
//              Effect-load time with compile exit 0 on our side.
// Exercises  : fwidth() and ddx() in the PS body, smoothstep-based edge AA.
// Regression : Issue #139 (missing GL_OES_standard_derivatives header).
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

    // The #139 shape: derivative-based edge antialiasing. fwidth is the token
    // mgfxc's scan never met (SPIRV-Cross emits it verbatim); ddx covers the
    // classic pair.
    float dist = abs(input.TexCoord.x - 0.5);
    float w    = fwidth(input.TexCoord.x) + abs(ddx(input.TexCoord.y));
    float edge = smoothstep(0.0, max(w, 0.0001), dist - 0.25);

    return float4(tex.rgb * edge, tex.a);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
