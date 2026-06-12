// Phase 43C (F4) — a cbuffer bound by BOTH stages.
//
// The VS reads WorldViewProjection, the PS reads DiffuseColor, both from the ONE
// `cbuffer SharedParams`. mgfxc's model (pinned by its golden) emits a SEPARATE
// per-stage record for this: a `vs_uniforms_vec4` AND a `ps_uniforms_vec4`, each
// listing the parameters at that stage's register offsets. Before Phase 43C
// ShadowDusk deduped the cbuffer ACROSS stages into a single `ps_uniforms_vec4`
// record while the VS GLSL read `vs_uniforms_vec4[]` — MonoGame never uploaded
// the VS array, so the transform silently read zero (every vertex collapsed to
// the origin; nothing rendered).
//
// Uses TRUE SV_Position (not the `#define SV_POSITION POSITION` alias) so DXC
// emits a real gl_Position output — same rationale as VsTransformColorTexture.

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

cbuffer SharedParams
{
    float4x4 WorldViewProjection;
    float4   DiffuseColor;
};

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
    output.Position = mul(input.Position, WorldViewProjection);
    output.Color    = input.Color;
    output.TexCoord = input.TexCoord;
    return output;
}

float4 MainPS(VertexShaderOutput input) : SV_Target0
{
    return tex2D(SpriteTextureSampler, input.TexCoord) * input.Color * DiffuseColor;
}

technique SpriteDrawing
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
