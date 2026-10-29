// Matrix-interpolant packing: #pragma pack_matrix(row_major) does not change an interpolant: fxc still packs it by columns.
// The two stages declare the interpolant differently, so the render shows the register layout
// fxc gives it; the mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.
#pragma pack_matrix(row_major)
float Mode;
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Binormal : BINORMAL0; float3 Tangent : TANGENT0; };
struct VSOut { float4 Position : POSITION0; float3x3 M : TEXCOORD2; };
struct PSIn { float4 Position : POSITION0; float3 a : TEXCOORD2; float3 b : TEXCOORD3; float3 c : TEXCOORD4; };
VSOut VS(VSIn i) { VSOut o; o.Position = i.Position; o.M = float3x3(i.Tangent, i.Binormal, i.Normal); return o; }
float4 PS(PSIn i) : COLOR0 { if (Mode < 0.5) return float4(i.a, 1); if (Mode < 1.5) return float4(i.b, 1); return float4(i.c, 1); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
