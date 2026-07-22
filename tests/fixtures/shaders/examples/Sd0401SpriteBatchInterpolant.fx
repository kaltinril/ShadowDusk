// =============================================================================
// Sd0401SpriteBatchInterpolant.fx  —  ShadowDusk fresh lint fixture (SD0401)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 53).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the SD0401 portability warning — a pass with NO vertex shader
//              whose pixel shader reads TEXCOORD1, an interpolant SpriteBatch's
//              built-in SpriteEffect vertex shader never writes (it provides
//              only COLOR0 and TEXCOORD0). Drawn with SpriteBatch on OpenGL, the
//              program fails to link on strict drivers at the FIRST draw with
//              the engine's generic "Shader Compilation Failed" exception — the
//              exact field-report shape. The compile must SUCCEED (the shader is
//              valid, and fine with a custom VS) while warning SD0401.
// Exercises  : PS-only pass, PS input reading TEXCOORD0 + TEXCOORD1.
// Regression : Phase 53 SD0401 lint (SpriteBatch varying compatibility).
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
    float4 Color     : COLOR0;
    float2 TexCoord  : TEXCOORD0;
    float2 TexCoord1 : TEXCOORD1; // SpriteBatch's VS never writes this
};

float4 MainPS(PixelInput input) : COLOR0
{
    float4 a = tex2D(SpriteTextureSampler, input.TexCoord);
    float4 b = tex2D(SpriteTextureSampler, input.TexCoord1);
    return lerp(a, b, 0.5) * input.Color;
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
