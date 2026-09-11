// A6 residue sweep sample, derived from tests/fixtures/shaders/third-party/MonoGame/TextureArrayEffect.fx.
// Exercises: Texture2DArray.Sample (a 3rd-coordinate array-slice sample, distinct
// from the plain Texture2D.Sample in the shipped corpus), SV_VertexID, integer bit
// shift/mask (`<<`, `&`), a `(uint)` cast, a VS+PS pair with the VS itself
// generating a full-screen triangle from just the vertex id (no vertex-buffer
// input struct at all — every other VS in the shipped corpus takes a POSITION/
// COLOR0 vertex-input struct).
Texture2DArray SliceTexture;
SamplerState SliceSampler;

struct VSOutput
{
    float4 PositionPS : SV_Position;
    float2 TexCoord : TEXCOORD0;
};

struct PSInput
{
    float4 PositionSS : SV_Position;
    float2 TexCoord : TEXCOORD0;
};

[shader("vertex")]
VSOutput VS_Main(uint VertexID : SV_VertexID)
{
    VSOutput output;
    output.TexCoord = float2((VertexID << 1) & 2, VertexID & 2);
    output.PositionPS = float4(output.TexCoord * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0f, 1.0f);
    return output;
}

[shader("fragment")]
float4 PS_Main(PSInput input) : SV_Target
{
    return SliceTexture.Sample(SliceSampler, float3(input.TexCoord, (uint) input.PositionSS.x % 4));
}
