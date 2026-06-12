// Phase 43C (F5, vertex-stage variant) — TWO cbuffers bound by the VERTEX stage.
//
// Same merge model as MultiCbuffer.fx but in the VS: both blocks fold into the
// one `vs_uniforms_vec4` register space (CameraParams' matrix at registers 0-3,
// AnimParams' PositionOffset/ColorScale at 4/5), and the .mgfx carries a single
// vs_uniforms_vec4 record whose offsets agree with the emitted GLSL.

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

cbuffer CameraParams
{
    float4x4 WorldViewProjection;
};

cbuffer AnimParams
{
    float4 PositionOffset;
    float4 ColorScale;
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
    output.Position = mul(input.Position + PositionOffset, WorldViewProjection);
    output.Color    = input.Color * ColorScale;
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
