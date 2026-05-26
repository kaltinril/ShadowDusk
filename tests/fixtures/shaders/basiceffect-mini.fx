float4x4 WorldViewProj;

struct VSInput  { float4 Position : POSITION; float4 Color : COLOR0; };
struct VSOutput { float4 Position : SV_POSITION; float4 Color : COLOR0; };

VSOutput VS_NoTex(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    output.Color    = input.Color;
    return output;
}

float4 PS_Vertex(VSOutput input) : SV_TARGET { return input.Color; }
float4 PS_White(VSOutput input)  : SV_TARGET { return float4(1,1,1,1); }
float4 PS_Flat(VSOutput input)   : SV_TARGET { return float4(0.5,0.5,0.5,1); }
float4 PS_Debug(VSOutput input)  : SV_TARGET { return float4(1,0,1,1); }

// Technique indices 0-3 must be distinct and ordered
technique Tech0 { pass P { VertexShader = compile vs_5_0 VS_NoTex(); PixelShader = compile ps_5_0 PS_Vertex(); } }
technique Tech1 { pass P { VertexShader = compile vs_5_0 VS_NoTex(); PixelShader = compile ps_5_0 PS_White();  } }
technique Tech2 { pass P { VertexShader = compile vs_5_0 VS_NoTex(); PixelShader = compile ps_5_0 PS_Flat();   } }
technique Tech3 { pass P { VertexShader = compile vs_5_0 VS_NoTex(); PixelShader = compile ps_5_0 PS_Debug();  } }
