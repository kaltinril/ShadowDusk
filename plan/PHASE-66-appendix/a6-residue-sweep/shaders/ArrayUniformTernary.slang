// A6 residue sweep sample, derived from tests/fixtures/shaders/ArrayUniform.fx.
// Exercises: literal-indexed float4[]/float[] array uniforms (a different array
// shape from GaussianBlurLoop.slang's loop-indexed read: this one is a chained
// ternary of literal indices, matching what a real ps_3_0-constrained shader has to
// do since D3D9 pixel shaders cannot index a constant array dynamically) plus a
// legacy `sampler2D X = sampler_state { Texture = <tex>; };`. Array-uniform
// reflection (Effect.Parameters["Colors"].Elements) is exactly the surface the
// -no-hlsl-pack-constant-buffer-elements flag (Phase 66 A3) was added to keep flat.
float4 Colors[4];
float Weights[4];

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

struct VertexShaderOutput
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

[shader("fragment")]
float4 MainPS(VertexShaderOutput input) : SV_Target
{
    float4 pixel = tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;

    float4 band = input.TextureCoordinates.x < 0.25 ? Colors[0] * Weights[0]
                : input.TextureCoordinates.x < 0.50 ? Colors[1] * Weights[1]
                : input.TextureCoordinates.x < 0.75 ? Colors[2] * Weights[2]
                :                                     Colors[3] * Weights[3];

    return pixel * band;
}
