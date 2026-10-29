// Matrix-interpolant packing: VSA writes TEXCOORD0 as a row_major float3x3, VSB as a column_major one, and each
// technique pairs a VS with the OTHER PS, so the render shows each stage's own layout (crossed pairing).
// The mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Binormal : BINORMAL0; float3 Tangent : TANGENT0; };
struct OutA { float4 Position : POSITION0; row_major float3x3 M : TEXCOORD0; };
struct OutB { float4 Position : POSITION0; float3x3 M : TEXCOORD0; };
OutA VSA(VSIn i) { OutA o; o.Position = i.Position; o.M = float3x3(i.Tangent, i.Binormal, i.Normal); return o; }
OutB VSB(VSIn i) { OutB o; o.Position = i.Position; o.M = float3x3(i.Tangent, i.Binormal, i.Normal); return o; }
float4 PSA(OutA i) : COLOR0 { return float4(i.M[1], 1); }
float4 PSB(OutB i) : COLOR0 { return float4(i.M[1], 1); }
technique A { pass P { VertexShader = compile vs_3_0 VSA(); PixelShader = compile ps_3_0 PSB(); } }
technique B { pass P { VertexShader = compile vs_3_0 VSB(); PixelShader = compile ps_3_0 PSA(); } }
