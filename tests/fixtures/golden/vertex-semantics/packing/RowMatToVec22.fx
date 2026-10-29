// Matrix-interpolant packing: row_major float2x2 read as two float2.
// The two stages declare the interpolant differently, so the render shows the register layout
// fxc gives it; the mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.

float Mode;
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Binormal : BINORMAL0; float3 Tangent : TANGENT0; };
struct VSOut { float4 Position : POSITION0; row_major float2x2 M : TEXCOORD1; };
struct PSIn { float4 Position : POSITION0; float2 a : TEXCOORD1; float2 b : TEXCOORD2; };
VSOut VS(VSIn i) { VSOut o; o.Position = i.Position; o.M = float2x2(i.Tangent.xy, i.Binormal.yz); return o; }
float4 PS(PSIn i) : COLOR0 { if (Mode < 0.5) return float4(i.a, 0, 1); return float4(i.b, 0, 1); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
