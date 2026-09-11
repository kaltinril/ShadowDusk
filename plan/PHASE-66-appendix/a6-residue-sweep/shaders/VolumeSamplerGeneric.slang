// A6 residue sweep sample, derived from tests/fixtures/shaders/examples/ExVolumeTextureHidef.fx.
// Exercises: Texture3D.Sample — the other non-2D sampler generality-guard case
// (see CubeSamplerGeneric.slang's comment for why this class matters).
Texture3D VolumeTexture;
SamplerState VolumeSampler;

struct VertexShaderOutput
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
    float3 TexCoord : TEXCOORD0;
};

[shader("fragment")]
float4 MainPS(VertexShaderOutput input) : SV_Target
{
    return VolumeTexture.Sample(VolumeSampler, input.TexCoord);
}
