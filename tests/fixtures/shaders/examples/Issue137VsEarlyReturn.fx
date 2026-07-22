// =============================================================================
// Issue137VsEarlyReturn.fx  —  ShadowDusk fresh regression fixture (issue #137)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #137).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the issue-#137 class — a VERTEX-stage (inlined) helper with a
//              CONDITIONAL EARLY RETURN. SPIRV-Cross renders the early return as
//              a one-shot `do { ... } while(false);` wrapper; GLSL ES 1.00
//              Appendix A forbids do-while entirely (the issue-#107 shape), so
//              the effect failed to load on WebGL1 / KNI Reach with compile exit
//              0 and no diagnostic. Rule 9b must now lower the wrapper to the
//              Appendix-A-allowed one-shot for-loop in the VS too — and the
//              posFixup lines must still run on every path (which is exactly why
//              Rule 9a's break-to-early-`return;` unwrap stays pixel-only).
// Exercises  : conditional early return in a VS helper, VS mul-transform,
//              SpriteBatch-compatible vertex set, textured + tinted PS.
// Regression : Issue #137 consequence 2 (VS early-return helper ships do-while).
// =============================================================================
#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4x4 WorldViewProjection;

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

struct VertexShaderInput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

struct VertexShaderOutput
{
    float4 Position : SV_Position;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

// The #137/#107 shape: a helper with a conditional early return, called from the
// vertex entry point. DXC inlines it; SPIRV-Cross expresses the early return as a
// one-shot do-while the GL dialect rewrite must lower.
float4 NudgeIfInFront(float4 world)
{
    if (world.w <= 0.0)
    {
        return world; // early out: leave degenerate positions untouched
    }
    world.xy += float2(0.001, 0.001) * world.w;
    return world;
}

VertexShaderOutput MainVS(VertexShaderInput input)
{
    VertexShaderOutput output = (VertexShaderOutput)0;
    output.Position = NudgeIfInFront(mul(input.Position, WorldViewProjection));
    output.Color    = input.Color;
    output.TexCoord = input.TexCoord;
    return output;
}

float4 MainPS(VertexShaderOutput input) : COLOR0
{
    return tex2D(SpriteTextureSampler, input.TexCoord) * input.Color;
}

technique SpriteDrawing
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
