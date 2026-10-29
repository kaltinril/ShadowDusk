// Matrix-interpolant packing: the row_major matrix is a member of a struct nested (without a semantic) in the output struct.
// The mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.
struct Inner { row_major float3x3 M : TEXCOORD0; };
struct VSOut { float4 Position : POSITION0; Inner I; };
VSOut VS(float4 p : POSITION0, float3 n : NORMAL0, float3 t : TANGENT0, float3 bn : BINORMAL0) { VSOut o; o.Position = p; o.I.M = float3x3(t, bn, n); return o; }
float Mode;
struct PSIn { float4 Position : POSITION0; float3 a : TEXCOORD0; float3 b : TEXCOORD1; float3 c : TEXCOORD2; };
float4 PS(PSIn i) : COLOR0 { if (Mode < 0.5) return float4(i.a, 1); if (Mode < 1.5) return float4(i.b, 1); return float4(i.c, 1); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
