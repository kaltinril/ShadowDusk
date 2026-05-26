float4 TintColor < string UIName = "Tint Color"; int UIOrder = 1; >;

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_POSITION; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = input.Position;
    return output;
}

float4 PS(VSOutput input) : SV_TARGET { return TintColor; }

technique Annotated
{
    pass Pass0
    {
        VertexShader = compile vs_5_0 VS();
        PixelShader  = compile ps_5_0 PS();
    }
}
