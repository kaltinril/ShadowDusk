// Phase 28 — VS-driven MonoGame effect fixture.
//
// A custom vertex shader that takes a float4x4 transform and the SpriteBatch-
// compatible vertex set (POSITION0 / COLOR0 / TEXCOORD0), plus a textured +
// tinted pixel shader. This exercises the full VS-side MonoGame-GL contract:
//   - a mat4 free-uniform (WorldViewProjection) expanded to 4 vs_uniforms_vec4
//     registers,
//   - the legacy attribute table (vs_v0/vs_v1/vs_v2 -> Position/Color/TexCoord),
//   - VS outputs carried over the varyings the PS reads (vFrontColor/vTexCoord0),
//   - gl_Position written from SV_Position.
//
// It deliberately uses TRUE SV_Position (not the legacy `#define SV_POSITION
// POSITION` form) so DXC emits a real gl_Position output the rewriter can lower;
// the older POSITION-aliased form produces a dead user-varying on the DXC path.

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
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
    float4 Position : SV_Position;
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
