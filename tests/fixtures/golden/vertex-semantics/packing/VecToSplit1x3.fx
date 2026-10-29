// Matrix-interpolant packing: three float written by the VS at TEXCOORD0..2, read by the PS as one float1x3.
// The mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Binormal : BINORMAL0; float3 Tangent : TANGENT0; };
struct VSOut { float4 Position : POSITION0; float a : TEXCOORD0; float b : TEXCOORD1; float c : TEXCOORD2; };
struct PSIn { float4 Position : POSITION0; float1x3 W : TEXCOORD0; };
VSOut VS(VSIn i) { VSOut o; o.Position = i.Position; o.a = i.Tangent.y; o.b = i.Normal.z; o.c = i.Binormal.x; return o; }
float4 PS(PSIn i) : COLOR0 { return float4(i.W[0][0], i.W[0][1], i.W[0][2], 1); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
