// Matrix-interpolant packing: four float3 written, read as a column_major float3x4.
// The two stages declare the interpolant differently, so the render shows the register layout
// fxc gives it; the mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.

float Mode;
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Binormal : BINORMAL0; float3 Tangent : TANGENT0; };
struct VSOut { float4 Position : POSITION0; float3 a : TEXCOORD0; float3 b : TEXCOORD1; float3 c : TEXCOORD2; float3 d : TEXCOORD3; };
struct PSIn { float4 Position : POSITION0; float3x4 M : TEXCOORD0; };
VSOut VS(VSIn i) { VSOut o; o.Position = i.Position; o.a = i.Tangent; o.b = i.Binormal; o.c = i.Normal; o.d = i.Normal.zxy; return o; }
float4 PS(PSIn i) : COLOR0 { if (Mode < 0.5) return i.M[0]; if (Mode < 1.5) return i.M[2]; return float4(i.M._m03_m13_m23, 1); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
