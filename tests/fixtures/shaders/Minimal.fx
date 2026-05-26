// Minimal.fx — used by Phase 8 + Phase 9 integration tests.
// Compiles for DirectX_11 and OpenGL without any textures or constant buffers.

struct VertexInput
{
    float4 Position : POSITION;
    float4 Color    : COLOR0;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
};

PixelInput VS(VertexInput input)
{
    PixelInput output;
    output.Position = input.Position;
    output.Color    = input.Color;
    return output;
}

float4 PS(PixelInput input) : SV_TARGET
{
    return input.Color;
}

technique Technique1
{
    pass Pass1
    {
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 PS();
    }
}
