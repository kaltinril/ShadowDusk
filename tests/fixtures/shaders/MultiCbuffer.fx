// Phase 43C (F5) — TWO cbuffers bound by the same (pixel) stage.
//
// mgfxc/MojoShader's model: D3D9 has a single float-constant register file per
// stage, so fxc allocates BOTH cbuffers into one register space and mgfxc emits
// ONE `ps_uniforms_vec4` record covering all members. Before Phase 43C ShadowDusk
// rewrote only the first SPIRV-Cross uniform block; the second shipped as a raw
// `layout(binding = 1, std140) uniform type_BlendParams { … }` block inside
// versionless legacy GLSL — a GL compile error at Effect-load time — while the
// compile exited 0, and the .mgfx carried two records both named
// `ps_uniforms_vec4` with overlapping offsets.

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

cbuffer TintParams
{
    float4 TintA;
};

cbuffer BlendParams
{
    float4 TintB;
    float  MixAmount;
};

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

float4 MainPS(VertexShaderOutput input) : COLOR
{
    float4 pixel = tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
    return pixel * lerp(TintA, TintB, MixAmount);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
