// A6 residue sweep sample, derived from tests/fixtures/shaders/examples/ExSampleGradHidef.fx.
// Exercises: Texture2D.SampleGrad — an explicit-gradient sample. DXC emits an
// explicit-gradient SPIR-V instruction and MonoGameGlslRewriter lowers it to the
// legacy texture2DGrad()/textureGrad() dance (Rule 6b) — a different code path from
// the plain .Sample() every shipped-corpus shader used.
Texture2D SpriteTexture;
SamplerState SpriteTextureSampler;

struct VertexShaderOutput
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

[shader("fragment")]
float4 MainPS(VertexShaderOutput input) : SV_Target
{
    return SpriteTexture.SampleGrad(SpriteTextureSampler, input.TexCoord,
        float2(0.01, 0.0), float2(0.0, 0.01));
}
