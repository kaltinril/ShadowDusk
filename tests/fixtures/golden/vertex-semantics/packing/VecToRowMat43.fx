// Matrix-interpolant packing: four float3 written, read as a row_major float4x3.
// The two stages declare the interpolant differently, so the render shows the register layout
// fxc gives it; the mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.

float Mode;
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Binormal : BINORMAL0; float3 Tangent : TANGENT0; };
struct VSOut { float4 Position : POSITION0; float3 a : TEXCOORD1; float3 b : TEXCOORD2; float3 c : TEXCOORD3; float3 d : TEXCOORD4; };
struct PSIn { float4 Position : POSITION0; row_major float4x3 M : TEXCOORD1; };
VSOut VS(VSIn i) { VSOut o; o.Position = i.Position; o.a = i.Tangent; o.b = i.Binormal; o.c = i.Normal; o.d = i.Normal.zxy; return o; }
float4 PS(PSIn i) : COLOR0 { float4 v = float4(0.5, 0.25, 0.125, 0.0625); if (Mode < 0.5) return float4(mul(v, i.M), 1); if (Mode < 1.5) return float4(i.M[3], 1); return float4(i.M._m02_m11_m20, 1); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
