// Phase 43C (F6) — float4/float ARRAY uniforms in the pixel stage.
//
// The PS picks a Colors[i] * Weights[i] band by the texture coordinate, with
// LITERAL element indices so fxc can compile it at ps_3_0 (D3D9 pixel shaders
// have no indexed constant reads; dynamic-index coverage is unit-level on the
// rewriter, where SPIRV-Cross's `[_40]` form takes the same code path).
//
// What this pins (both broken before Phase 43C):
//   - GLSL: `vec4 Colors[4];` members were skipped by the rewriter's member
//     regex, so the emitted GLSL still referenced the DELETED `_Globals` block
//     (`_Globals.Colors[1]`) — invalid GLSL, Effect-load failure, exit code 0.
//   - .mgfx: parameters carried Elements count 0 on EVERY target, so
//     `Effect.Parameters["Colors"].SetValue(Vector4[])` (and `.Elements[i]`)
//     was impossible beyond element 0 even on DirectX. The render harness sets
//     elements 1..3 from managed code — the smoking gun.

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4 Colors[4];
float  Weights[4];

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

    float4 band = input.TextureCoordinates.x < 0.25 ? Colors[0] * Weights[0]
                : input.TextureCoordinates.x < 0.50 ? Colors[1] * Weights[1]
                : input.TextureCoordinates.x < 0.75 ? Colors[2] * Weights[2]
                :                                     Colors[3] * Weights[3];

    return pixel * band;
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
