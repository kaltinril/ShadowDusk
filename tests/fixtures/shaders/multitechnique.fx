float4x4 WorldViewProj;

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_POSITION; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    return output;
}

float4 PSA(VSOutput i) : SV_TARGET { return float4(1, 0, 0, 1); }
float4 PSB(VSOutput i) : SV_TARGET { return float4(0, 1, 0, 1); }
float4 PSC(VSOutput i) : SV_TARGET { return float4(0, 0, 1, 1); }

technique TechA
{
    pass Pass0 { VertexShader = compile vs_5_0 VS(); PixelShader = compile ps_5_0 PSA(); }
}
technique TechB
{
    pass Pass0 { VertexShader = compile vs_5_0 VS(); PixelShader = compile ps_5_0 PSB(); }
}
technique TechC
{
    pass Pass0 { VertexShader = compile vs_5_0 VS(); PixelShader = compile ps_5_0 PSC(); }
}
