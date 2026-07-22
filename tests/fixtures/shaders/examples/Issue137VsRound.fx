// =============================================================================
// Issue137VsRound.fx  —  ShadowDusk fresh regression fixture (issue #137)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #137).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the issue-#137 class — HLSL round() in a VERTEX shader. The
//              body-lowering rules were pixel-stage-only, so a VS round() shipped
//              SPIRV-Cross's roundEven() in the vertex GLSL: a builtin absent
//              from GLSL ES 1.00 (WebGL1 / KNI Reach) and rejected by Mesa's
//              strict versionless-1.10 front end — a silent Effect-load failure
//              with compile exit 0. Rule 8 must now lower it in the VS too.
// Exercises  : round() in a VS body (pixel-snapping the transformed position),
//              VS mul-transform, SpriteBatch-compatible vertex set, textured PS.
// Regression : Issue #137 consequence 1 (VS round() ships roundEven()).
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

VertexShaderOutput MainVS(VertexShaderInput input)
{
    VertexShaderOutput output = (VertexShaderOutput)0;
    float4 world = mul(input.Position, WorldViewProjection);

    // The #137 shape: round() in the vertex stage (snap the transformed position
    // to a coarse grid). Must reach the GL output as floor(x + 0.5), never as
    // roundEven().
    world.xy = round(world.xy * 8.0) / 8.0;

    output.Position = world;
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
