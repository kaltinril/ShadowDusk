// A6 residue sweep sample, derived from tests/fixtures/shaders/examples/ExCubeSamplerHidef.fx.
// Exercises: TextureCube.Sample — a non-2D sampler type. MonoGameGlslRewriter's
// OpenGL uniform/sampler lowering models only sampler2D and is DESIGNED to reject
// anything else loudly (SD0210, see that fixture's own header comment); the point
// here is confirming the real-slangc route hits the SAME loud rejection and does
// not silently produce something that "compiles" but loads wrong.
TextureCube EnvMap;
SamplerState EnvSampler;

struct VertexShaderOutput
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
    float3 Direction : TEXCOORD0;
};

[shader("fragment")]
float4 MainPS(VertexShaderOutput input) : SV_Target
{
    return EnvMap.Sample(EnvSampler, input.Direction);
}
