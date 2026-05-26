float4x4 WorldViewProj;

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_POSITION; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    return output;
}

float4 PS(VSOutput input) : SV_TARGET { return float4(1, 1, 1, 0.5); }

technique RenderStates
{
    pass Pass0
    {
        VertexShader        = compile vs_5_0 VS();
        PixelShader         = compile ps_5_0 PS();
        CullMode            = None;
        AlphaBlendEnable    = True;
        DepthBufferEnable   = False;
    }
}
