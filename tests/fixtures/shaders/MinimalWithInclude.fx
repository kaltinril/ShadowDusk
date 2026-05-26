// MinimalWithInclude.fx — used by Phase 8 integration test for /I include-path flag.
// The included header lives in a sibling 'includes/' directory.

#include "TestHelper.fxh"

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
    output.Position = ApplyIdentity(input.Position);
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
