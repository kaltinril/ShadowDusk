// =============================================================================
// ExTextureNamedTexture.fx  —  ShadowDusk fresh example fixture (Phase 45, B5)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 45) on
//              2026-06-17. Project-owned (same license as the repository). NOT
//              derived from any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Pin Phase-45 bug B5 — a modern resource whose VARIABLE NAME is a
//              legacy texture keyword, e.g. 'Texture2D Texture : register(t0);'.
//              The legacy-texture rewrite matched on the keyword 'Texture' (a map
//              key) and the following identifier, and its old guard only declined
//              the TEMPLATED form ('Texture2D<float4> Texture …', prev token '>').
//              In NAME position after a type ('Texture2D Texture') the previous
//              code token is an Identifier, so the rewrite wrongly fired and turned
//              the declaration into the broken 'Texture2D Texture2D register;'. The
//              fix declines the rewrite when the keyword's preceding code token is
//              an Identifier or '>' (i.e. it is in name position, never a legacy
//              type declaration). Genuine 'texture Foo;' (keyword at statement
//              start) still rewrites.
// Exercises  : 'Texture2D Texture : register(t0);' (resource literally named
//              'Texture'), a 'SamplerState' literally named 'Sampler', the modern
//              'Texture.Sample(Sampler, uv)' method call.
// Regression : Before the fix, a resource named 'Texture' (or another legacy
//              texture keyword) was corrupted into invalid HLSL.
// Targets    : OpenGL + DirectX_11. 'Texture2D Name : register(t0);' + '.Sample'
//              is SM4 syntax (the MGFX RewriteToSm4 path); the FNA SM<=3 path does
//              not use it, so FNA is N/A for this fixture.
// =============================================================================
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// The B5 shape: a modern Texture2D whose variable name IS the legacy keyword
// 'Texture', plus a SamplerState whose name starts with the legacy keyword too.
Texture2D    Texture : register(t0);
SamplerState Sampler : register(s0);

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR0
{
    // Modern method-syntax sample through the resource literally named 'Texture'.
    return Texture.Sample(Sampler, input.TexCoord) * input.Color;
}

technique TextureNamedTextureExample
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
