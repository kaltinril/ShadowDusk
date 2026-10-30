// Matrix-interpolant packing: column_major float4x3 (three registers of four) read as three float4.
// The two stages declare the interpolant differently, so the render shows the register layout
// fxc gives it; the mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.

float Mode;
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Binormal : BINORMAL0; float3 Tangent : TANGENT0; };
struct VSOut { float4 Position : POSITION0; float4x3 M : TEXCOORD1; };
struct PSIn { float4 Position : POSITION0; float4 a : TEXCOORD1; float4 b : TEXCOORD2; float4 c : TEXCOORD3; };
VSOut VS(VSIn i) { VSOut o; o.Position = i.Position; o.M = float4x3(i.Tangent, i.Binormal, i.Normal, i.Normal.zxy); return o; }
float4 PS(PSIn i) : COLOR0 { if (Mode < 0.5) return i.a; if (Mode < 1.5) return i.b; return i.c; }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
