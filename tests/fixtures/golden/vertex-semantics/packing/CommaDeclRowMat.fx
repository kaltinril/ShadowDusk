// Matrix-interpolant packing: comma declarators: row_major applies to both A and B.
// The mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.
float Mode;
struct VSOut { float4 Position : POSITION0; row_major float3x3 A : TEXCOORD0, B : TEXCOORD3; };
struct PSIn { float4 Position : POSITION0; float3 a : TEXCOORD3; float3 b : TEXCOORD4; float3 c : TEXCOORD5; };
VSOut VS(float4 p : POSITION0, float3 n : NORMAL0, float3 t : TANGENT0, float3 bn : BINORMAL0) { VSOut o; o.Position = p; o.A = float3x3(n, n, n); o.B = float3x3(t, bn, n); return o; }
float4 PS(PSIn i) : COLOR0 { if (Mode < 0.5) return float4(i.a, 1); if (Mode < 1.5) return float4(i.b, 1); return float4(i.c, 1); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
