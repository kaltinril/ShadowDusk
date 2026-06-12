// Phase 43C (F6, vertex-stage variant) — float4x4 and float4 ARRAY uniforms in
// the VS, blending BOTH elements of both arrays (skinning-style `Bones[]`): the
// output is right only if every element lands at its exact register offset, so
// the render proves per-element modelling AND upload (the pre-43C Elements gap
// made any element beyond 0 unreachable).
//
// DELIBERATELY references EVERY element. A statically-PARTIALLY-read array
// (e.g. only Bones[1]) is broken in mgfxc+MonoGame GL ITSELF: fxc only
// references the used registers, MojoShader then emits a COMPACTED uniform
// array (vs_c4..c7,c9 -> vs_uniforms_vec4[0..4]) while mgfxc's cbuffer record
// keeps the full 160-byte layout — MonoGame uploads the full buffer into the
// short array, so element 0's data lands where the shader reads element 1
// (verified: the mgfxc golden for that shape renders garbage in real MonoGame
// 3.8.2 while ShadowDusk's full-layout output renders the source semantics
// correctly — see the Phase 43 doc, F6 as-built).

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4x4 Bones[2];
float4   PosOffsets[2];

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
    float4 p0 = mul(input.Position + PosOffsets[0], Bones[0]);
    float4 p1 = mul(input.Position + PosOffsets[1], Bones[1]);
    output.Position = p0 * 0.35 + p1 * 0.65;
    output.Color    = input.Color;
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
