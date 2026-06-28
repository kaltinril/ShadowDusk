// =============================================================================
// MonoGameInCode-Grayscale.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : Gum (vchelaru/gum)
//   Repo       : https://github.com/vchelaru/gum
//   Commit     : 771bc5c3d18e97db65a45a803763946d17b7d1ea
//   Upstream   : Samples/MonoGameGumInCode/MonoGameGumInCode/Content/Grayscale.fx
//   License    : MIT - Copyright (c) 2013-2024 FlatRedBall, LLC (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Modern SpriteEffect-style grayscale post-FX: vs/ps_4_0_level_9_1
//                profiles, Texture2D + sampler2D + sampler_state, : COLOR0 output,
//                PS-only technique, dot-luminance.
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

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR0
{
    float4 color = tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
    float gray = dot(color.rgb, float3(0.299, 0.587, 0.114));
    return float4(gray, gray, gray, color.a);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
