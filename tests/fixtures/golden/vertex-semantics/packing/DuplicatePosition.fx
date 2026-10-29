// Two vertex inputs naming one element (POSITION0 and SV_POSITION): fxc gives them ONE input
// register, so mgfxc writes one attribute record and both read it.
struct VSIn { float4 Position : POSITION0; float4 Again : SV_POSITION; float3 Normal : NORMAL0; };
struct VSOut { float4 Position : POSITION0; float4 Color : TEXCOORD0; };
VSOut VS(VSIn i) { VSOut o; o.Position = i.Position; o.Color = float4(i.Again.xy * 0.5 + 0.5, i.Normal.z, 1); return o; }
float4 PS(VSOut i) : COLOR0 { return i.Color; }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }