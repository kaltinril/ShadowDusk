// Issue #70 follow-up — the LEGACY ': POSITION' vertex output variant of
// VsTransformColorTexture.fx. Identical uniform/attribute contract (WorldViewProjection,
// Tint, SpriteTexture; POSITION0 / COLOR0 / TEXCOORD0), but the vertex shader's position
// output carries the D3D9 POSITION semantic via `#define SV_POSITION POSITION` — the form
// the stock MonoGame OpenGL effect template emits.
//
// ShadowDusk's frontend is DXC (Shader Model 6), where ONLY `: SV_Position` is the builtin
// clip position; a `: POSITION` output is an ordinary user varying. Without the rewriter's
// position-semantic mapping (IsPositionSemantic -> gl_Position), this would write the
// transform to a dead varying and leave gl_Position UNWRITTEN — silently-broken geometry.
// The validation harness renders THIS and the true-SV_Position VsTransformColorTexture
// through real MonoGame and asserts they are pixel-identical: proof the legacy form both
// LOADS and renders correctly (mgfxc maps `: POSITION` to gl_Position natively in SM3).

#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define SV_POSITION SV_Position
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4x4 WorldViewProjection;
float4   Tint;

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
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

VertexShaderOutput MainVS(VertexShaderInput input)
{
    VertexShaderOutput output = (VertexShaderOutput)0;
    output.Position = mul(input.Position, WorldViewProjection);
    output.Color    = input.Color * Tint;
    output.TexCoord = input.TexCoord;
    return output;
}

float4 MainPS(VertexShaderOutput input) : SV_Target0
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
